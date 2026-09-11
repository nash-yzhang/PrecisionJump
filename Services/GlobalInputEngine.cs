using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using PrecisionJump.Models;
using PrecisionJump.Views;

namespace PrecisionJump.Services;

public sealed class GlobalInputEngine : IDisposable
{
    private const double PreviewFrameIntervalSeconds = 1d / 60;

    private enum PositionCommand
    {
        None,
        Save,
        Recall
    }

    private sealed record InputSettings(
        bool Enabled,
        bool PositionMarksEnabled,
        int[] ScreenJumpSequence,
        int[] SavePositionSequence,
        int[] RecallPositionSequence,
        double SequenceTimeoutSeconds,
        double SelectionDistance,
        int MaximumZoomLevel,
        bool RecordMouseEvents)
    {
        internal static InputSettings From(AppSettings settings) => new(
            settings.Enabled,
            settings.PositionMarksEnabled,
            settings.ScreenJumpSequence
                .Select(token => token.VirtualKey)
                .ToArray(),
            settings.SavePositionSequence
                .Select(token => token.VirtualKey)
                .ToArray(),
            settings.RecallPositionSequence
                .Select(token => token.VirtualKey)
                .ToArray(),
            settings.SequenceTimeoutSeconds,
            settings.SelectionDistance,
            settings.MaximumZoomLevel,
            settings.RecordMouseEvents);
    }

    private readonly AppSettings _settings;
    private readonly Dispatcher _dispatcher;
    private readonly NativeMethods.HookProc _keyboardCallback;
    private readonly NativeMethods.HookProc _mouseCallback;
    private readonly SequenceMatcher _screenJumpMatcher = new();
    private readonly SequenceMatcher _savePositionMatcher = new();
    private readonly SequenceMatcher _recallPositionMatcher = new();
    private readonly HashSet<int> _heldKeys = [];
    private readonly HashSet<int> _suppressedShortcutKeys = [];
    private readonly ScreenOverlayMap _overlayMap;
    private readonly MouseEventRecorder _mouseEventRecorder;
    private readonly MouseMoveInjector _mouseMoveInjector;
    private readonly LowLevelHookHost _hookHost;

    private DispatcherTimer? _scaleAnimationTimer;
    private readonly DispatcherTimer _previewDelayTimer;
    private InputSettings _inputSettings;
    private IReadOnlyList<DisplayMonitor> _displaySnapshot;
    private NineGridSession? _gestureSession;
    private int? _screenJumpReleaseKey;
    private PositionCommand _pendingPositionCommand;
    private long _pendingPositionCommandAt;
    private bool _suppressLeftButtonUp;
    private bool _suppressRightButtonUp;
    private bool _suppressMiddleButtonUp;
    private bool _suppressXButtonUp;
    private NineGridPreview? _queuedPreview;
    private int _previewRenderQueued;
    private long _lastPreviewRenderedAt;
    private long _lastMoveDiagnosticAt;
    private Point? _lastObservedCursorPosition;
    private ModeHudWindow? _hud;
    private int _isCapturingKey;
    private int _jumpActive;
    private volatile bool _disposed;

    public GlobalInputEngine(AppSettings settings, Dispatcher dispatcher)
    {
        _settings = settings;
        _dispatcher = dispatcher;
        _keyboardCallback = KeyboardHookCallback;
        _mouseCallback = MouseHookCallback;
        _overlayMap = new ScreenOverlayMap(dispatcher);
        _displaySnapshot = _overlayMap.Displays.ToArray();
        _overlayMap.DisplaysInvalidated += OverlayMapOnDisplaysInvalidated;
        _overlayMap.DisplaysChanged += OverlayMapOnDisplaysChanged;
        _mouseEventRecorder = new MouseEventRecorder();
        _mouseMoveInjector = new MouseMoveInjector();
        _previewDelayTimer = new DispatcherTimer(
            DispatcherPriority.Render,
            dispatcher);
        _previewDelayTimer.Tick += PreviewDelayTimerOnTick;
        _hookHost = new LowLevelHookHost(
            _keyboardCallback,
            _mouseCallback);
        _inputSettings = InputSettings.From(settings);
        _settings.PropertyChanged += SettingsOnPropertyChanged;
    }

    public event Action<bool>? JumpStateChanged;

    public bool JumpActive => Volatile.Read(ref _jumpActive) != 0;
    public bool IsCapturingKey
    {
        get => Volatile.Read(ref _isCapturingKey) != 0;
        set => Volatile.Write(ref _isCapturingKey, value ? 1 : 0);
    }

    public void Start()
    {
        DiagnosticLog("ENGINE STARTING");
        _hookHost.Start();
        _scaleAnimationTimer = new DispatcherTimer(
            DispatcherPriority.Background,
            _hookHost.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _scaleAnimationTimer.Tick += ScaleAnimationTimerOnTick;
        DiagnosticLog("ENGINE STARTED");
    }

    public void PrepareForShortcutCapture()
    {
        _hookHost.Post(
            () =>
            {
                CancelGesture(restoreOrigin: true, showFeedback: false);
                _screenJumpMatcher.Reset();
                ResetPositionShortcuts();
                _suppressedShortcutKeys.Clear();
            },
            DispatcherPriority.Send);
    }

    private nint KeyboardHookCallback(int code, nint wParam, nint lParam)
    {
        if (code < 0)
        {
            return NativeMethods.CallNextHookEx(0, code, wParam, lParam);
        }

        try
        {
            var message = wParam.ToInt32();
            var isDown = message is NativeMethods.WmKeyDown or NativeMethods.WmSysKeyDown;
            var isUp = message is NativeMethods.WmKeyUp or NativeMethods.WmSysKeyUp;
            if (!isDown && !isUp)
            {
                return NativeMethods.CallNextHookEx(0, code, wParam, lParam);
            }

            var data = Marshal.PtrToStructure<NativeMethods.KeyboardHookData>(lParam);
            if (ProcessKeyboard(
                KeyNames.Normalize((int)data.VirtualKey),
                isDown,
                isUp))
            {
                return 1;
            }
        }
        catch
        {
            FailOpen();
        }

        return NativeMethods.CallNextHookEx(0, code, wParam, lParam);
    }

    private nint MouseHookCallback(int code, nint wParam, nint lParam)
    {
        if (code < 0)
        {
            return NativeMethods.CallNextHookEx(0, code, wParam, lParam);
        }

        try
        {
            var message = wParam.ToInt32();
            var hookData =
                Marshal.PtrToStructure<NativeMethods.MouseHookData>(lParam);
            var isInjected =
                (hookData.Flags & NativeMethods.LlmhfInjected) != 0;
            var hookPosition = new Point(
                hookData.Position.X,
                hookData.Position.Y);
            if (isInjected && message == NativeMethods.WmMouseMove)
            {
                _lastObservedCursorPosition = hookPosition;
                // The hook position is the cursor position Windows actually
                // applied. It can differ from the requested absolute target
                // after virtual-desktop normalization, so it is the only safe
                // baseline for the next physical delta.
                _mouseMoveInjector.ObservePosition(hookPosition);
            }
            if (_gestureSession is not null)
            {
                if (!isInjected && message != NativeMethods.WmMouseMove)
                {
                    RecordMouseHookEvent(
                        message,
                        hookData,
                        jumping: true);
                }

                switch (message)
                {
                    case NativeMethods.WmMouseMove:
                    {
                        if (isInjected)
                        {
                            return NativeMethods.CallNextHookEx(
                                0,
                                code,
                                wParam,
                                lParam);
                        }

                        var physicalDelta =
                            _mouseMoveInjector.PhysicalDeltaFrom(
                                hookPosition);
                        var rawDeltaX = physicalDelta.X;
                        var rawDeltaY = physicalDelta.Y;
                        var inputSettings = Volatile.Read(ref _inputSettings);
                        var interaction = _gestureSession.MoveContinuous(
                            rawDeltaX,
                            rawDeltaY,
                            inputSettings.SelectionDistance);
                        if (interaction.JumpTarget is Point target)
                        {
                            _mouseMoveInjector.Queue(target);
                            RecordMouseEvent(
                                target,
                                "move",
                                jumping: true,
                                rawDeltaX,
                                rawDeltaY);
                        }
                        QueuePreview(interaction.Preview);
                        LogContinuousMove(
                            rawDeltaX,
                            rawDeltaY,
                            interaction.Preview);
                        return 1;
                    }
                    case NativeMethods.WmMouseWheel:
                    {
                        var delta = unchecked(
                            (short)(hookData.MouseData >> 16));
                        AdjustDepth(
                            delta,
                            new Point(
                                hookData.Position.X,
                                hookData.Position.Y));
                        return 1;
                    }
                    case NativeMethods.WmMouseHorizontalWheel:
                        return 1;
                    case NativeMethods.WmLeftButtonDown:
                        _suppressLeftButtonUp = true;
                        DiagnosticLog("LEFT CLICK IGNORED");
                        return 1;
                    case NativeMethods.WmLeftButtonUp:
                        _suppressLeftButtonUp = false;
                        return 1;
                    case NativeMethods.WmRightButtonDown:
                        _suppressRightButtonUp = true;
                        DiagnosticLog("RIGHT CLICK CANCEL");
                        CancelGesture(restoreOrigin: true, showFeedback: true);
                        return 1;
                    case NativeMethods.WmRightButtonUp:
                        _suppressRightButtonUp = false;
                        return 1;
                    case NativeMethods.WmMiddleButtonDown:
                        _suppressMiddleButtonUp = true;
                        return 1;
                    case NativeMethods.WmMiddleButtonUp:
                        _suppressMiddleButtonUp = false;
                        return 1;
                    case NativeMethods.WmXButtonDown:
                        _suppressXButtonUp = true;
                        return 1;
                    case NativeMethods.WmXButtonUp:
                        _suppressXButtonUp = false;
                        return 1;
                }
            }
            else
            {
                if (!isInjected)
                {
                    var rawDeltaX = 0;
                    var rawDeltaY = 0;
                    if (message == NativeMethods.WmMouseMove)
                    {
                        if (_lastObservedCursorPosition is Point previous)
                        {
                            rawDeltaX = hookPosition.X - previous.X;
                            rawDeltaY = hookPosition.Y - previous.Y;
                        }
                        _lastObservedCursorPosition = hookPosition;
                    }
                    RecordMouseHookEvent(
                        message,
                        hookData,
                        jumping: false,
                        rawDeltaX,
                        rawDeltaY);
                }

                if (message == NativeMethods.WmLeftButtonUp && _suppressLeftButtonUp)
                {
                    _suppressLeftButtonUp = false;
                    return 1;
                }
                if (message == NativeMethods.WmRightButtonUp && _suppressRightButtonUp)
                {
                    _suppressRightButtonUp = false;
                    return 1;
                }
                if (message == NativeMethods.WmMiddleButtonUp && _suppressMiddleButtonUp)
                {
                    _suppressMiddleButtonUp = false;
                    return 1;
                }
                if (message == NativeMethods.WmXButtonUp && _suppressXButtonUp)
                {
                    _suppressXButtonUp = false;
                    return 1;
                }
            }
        }
        catch
        {
            FailOpen();
        }

        return NativeMethods.CallNextHookEx(0, code, wParam, lParam);
    }

    private void FailOpen()
    {
        _gestureSession = null;
        _screenJumpReleaseKey = null;
        _screenJumpMatcher.Reset();
        ResetPositionShortcuts();
        _suppressedShortcutKeys.Clear();
        _suppressLeftButtonUp = false;
        _suppressRightButtonUp = false;
        _suppressMiddleButtonUp = false;
        _suppressXButtonUp = false;
        _scaleAnimationTimer?.Stop();
        Volatile.Write(ref _jumpActive, 0);
        Interlocked.Exchange(ref _queuedPreview, null);
        try
        {
            _dispatcher.BeginInvoke(
                _overlayMap.HideAll,
                DispatcherPriority.Send);
            NotifyJumpStateChanged(false);
        }
        catch
        {
            // Shutdown can dispose the UI dispatcher while a hook is returning.
        }
    }

    private void RecordMouseHookEvent(
        int message,
        NativeMethods.MouseHookData data,
        bool jumping,
        int rawDeltaX = 0,
        int rawDeltaY = 0)
    {
        var eventName = message switch
        {
            NativeMethods.WmMouseMove => "move",
            NativeMethods.WmLeftButtonDown => "left_down",
            NativeMethods.WmLeftButtonUp => "left_up",
            NativeMethods.WmRightButtonDown => "right_down",
            NativeMethods.WmRightButtonUp => "right_up",
            NativeMethods.WmMiddleButtonDown => "middle_down",
            NativeMethods.WmMiddleButtonUp => "middle_up",
            NativeMethods.WmMouseWheel =>
                unchecked((short)(data.MouseData >> 16)) >= 0
                    ? "wheel_up"
                    : "wheel_down",
            NativeMethods.WmMouseHorizontalWheel =>
                unchecked((short)(data.MouseData >> 16)) >= 0
                    ? "wheel_right"
                    : "wheel_left",
            NativeMethods.WmXButtonDown =>
                ((data.MouseData >> 16) & 0xffff) == 1
                    ? "x1_down"
                    : "x2_down",
            NativeMethods.WmXButtonUp =>
                ((data.MouseData >> 16) & 0xffff) == 1
                    ? "x1_up"
                    : "x2_up",
            _ => null
        };
        if (eventName is null)
        {
            return;
        }

        RecordMouseEvent(
            new Point(data.Position.X, data.Position.Y),
            eventName,
            jumping,
            rawDeltaX,
            rawDeltaY);
    }

    private void RecordMouseEvent(
        Point position,
        string eventName,
        bool jumping,
        int rawDeltaX = 0,
        int rawDeltaY = 0)
    {
        if (Volatile.Read(ref _inputSettings).RecordMouseEvents)
        {
            _mouseEventRecorder.Record(
                position,
                eventName,
                jumping,
                rawDeltaX,
                rawDeltaY);
        }
    }

    // true suppresses the key event from all other applications.
    private bool ProcessKeyboard(int virtualKey, bool isDown, bool isUp)
    {
        DiagnosticLog(
            $"KEY vk={virtualKey} down={isDown} up={isUp} capture={IsCapturingKey}");
        var firstKeyDown = isDown && _heldKeys.Add(virtualKey);
        if (isUp)
        {
            _heldKeys.Remove(virtualKey);
        }

        if (IsCapturingKey)
        {
            return false;
        }

        if (
            isUp
            && _gestureSession is not null
            && virtualKey == _screenJumpReleaseKey
        )
        {
            var suppressRelease = _suppressedShortcutKeys.Remove(virtualKey);
            ConfirmGesture();
            return suppressRelease;
        }

        if (isUp && _suppressedShortcutKeys.Remove(virtualKey))
        {
            return true;
        }

        if (isDown && _suppressedShortcutKeys.Contains(virtualKey))
        {
            return true;
        }

        if (_gestureSession is not null)
        {
            if (ShortcutPolicy.ShouldCancelActiveGesture(
                _screenJumpReleaseKey,
                virtualKey,
                firstKeyDown))
            {
                DiagnosticLog($"END FOR KEY vk={virtualKey}");
                CancelGesture(restoreOrigin: false, showFeedback: false);
                _screenJumpMatcher.Reset();
            }
            return false;
        }

        var inputSettings = Volatile.Read(ref _inputSettings);
        if (!firstKeyDown || !inputSettings.Enabled)
        {
            return false;
        }

        if (inputSettings.PositionMarksEnabled
            && TryProcessPositionShortcut(
            virtualKey,
            inputSettings,
            out var suppressPositionKey))
        {
            return suppressPositionKey;
        }

        var progress = _screenJumpMatcher.Consume(
            virtualKey,
            inputSettings.ScreenJumpSequence,
            TimeSpan.FromSeconds(inputSettings.SequenceTimeoutSeconds));
        if (progress is MatchProgress.None or MatchProgress.Prefix)
        {
            return false;
        }

        DiagnosticLog($"SEQUENCE MATCH final={virtualKey}");
        ResetPositionShortcuts();
        BeginGesture(finalKey: virtualKey);
        var suppressActivation =
            ShortcutPolicy.ShouldSuppressActivationKey(virtualKey);
        if (suppressActivation)
        {
            _suppressedShortcutKeys.Add(virtualKey);
        }
        return suppressActivation;
    }

    private bool TryProcessPositionShortcut(
        int virtualKey,
        InputSettings inputSettings,
        out bool suppressKey)
    {
        suppressKey = false;
        var now = Stopwatch.GetTimestamp();
        var timeout = TimeSpan.FromSeconds(
            inputSettings.SequenceTimeoutSeconds);

        if (_pendingPositionCommand != PositionCommand.None)
        {
            var elapsed = (now - _pendingPositionCommandAt)
                / (double)Stopwatch.Frequency;
            var pendingCommand = _pendingPositionCommand;
            _pendingPositionCommand = PositionCommand.None;
            _pendingPositionCommandAt = 0;

            if (elapsed <= timeout.TotalSeconds
                && MousePositionRegisters.TryGetRegister(
                    virtualKey,
                    out var register))
            {
                ExecutePositionCommand(pendingCommand, register);
                suppressKey = ShortcutPolicy.ShouldSuppressActivationKey(
                    virtualKey);
                if (suppressKey)
                {
                    _suppressedShortcutKeys.Add(virtualKey);
                }
                return true;
            }
        }

        var saveProgress = _savePositionMatcher.Consume(
            virtualKey,
            inputSettings.SavePositionSequence,
            timeout,
            now);
        var recallProgress = _recallPositionMatcher.Consume(
            virtualKey,
            inputSettings.RecallPositionSequence,
            timeout,
            now);
        var command = saveProgress == MatchProgress.Complete
            ? PositionCommand.Save
            : recallProgress == MatchProgress.Complete
                ? PositionCommand.Recall
                : PositionCommand.None;
        if (command == PositionCommand.None)
        {
            return false;
        }

        _pendingPositionCommand = command;
        _pendingPositionCommandAt = now;
        _screenJumpMatcher.Reset();
        _savePositionMatcher.Reset();
        _recallPositionMatcher.Reset();
        suppressKey = ShortcutPolicy.ShouldSuppressActivationKey(virtualKey);
        if (suppressKey)
        {
            _suppressedShortcutKeys.Add(virtualKey);
        }
        DiagnosticLog($"POSITION PREFIX command={command}");
        return true;
    }

    private void ExecutePositionCommand(PositionCommand command, char register)
    {
        if (command == PositionCommand.Save)
        {
            if (!NativeMethods.GetCursorPos(out var cursor))
            {
                QueueCompletion($"MARK {register} NOT SAVED", HudTone.Inactive);
                return;
            }

            _settings.SaveMousePosition(register, cursor.X, cursor.Y);
            DiagnosticLog($"POSITION SAVE register={register} point={cursor.X},{cursor.Y}");
            QueueCompletion($"MARK {register}  ·  SAVED");
            return;
        }

        if (!_settings.TryGetSavedMousePosition(register, out var saved))
        {
            DiagnosticLog($"POSITION EMPTY register={register}");
            QueueCompletion($"MARK {register}  ·  EMPTY", HudTone.Inactive);
            return;
        }

        var destination = MousePositionRegisters.ResolveDestination(
            new Point(saved.X, saved.Y),
            Volatile.Read(ref _displaySnapshot));
        _mouseMoveInjector.Queue(destination);
        RecordMouseEvent(
            destination,
            "mark_jump",
            jumping: false,
            rawDeltaX: 0,
            rawDeltaY: 0);
        DiagnosticLog(
            $"POSITION RECALL register={register} point={destination.X},{destination.Y}");
        QueueCompletion($"MARK {register}  ·  JUMP");
    }

    private void ResetPositionShortcuts()
    {
        _savePositionMatcher.Reset();
        _recallPositionMatcher.Reset();
        _pendingPositionCommand = PositionCommand.None;
        _pendingPositionCommandAt = 0;
    }

    private void BeginGesture(int finalKey)
    {
        if (!NativeMethods.GetCursorPos(out var cursor))
        {
            return;
        }

        var displays = Volatile.Read(ref _displaySnapshot);
        if (displays.Count == 0)
        {
            return;
        }

        _gestureSession = new NineGridSession(
            new Point(cursor.X, cursor.Y),
            displays);
        _lastObservedCursorPosition =
            new Point(cursor.X, cursor.Y);
        _mouseMoveInjector.ObservePosition(
            new Point(cursor.X, cursor.Y));
        _screenJumpReleaseKey = finalKey;
        Volatile.Write(ref _jumpActive, 1);
        DiagnosticLog(
            $"BEGIN origin={cursor.X},{cursor.Y} release={finalKey} displays={displays.Count}");
        QueuePreview(_gestureSession.Preview);
        NotifyJumpStateChanged(true);
    }

    private void LogContinuousMove(
        int deltaX,
        int deltaY,
        NineGridPreview preview)
    {
        var now = Stopwatch.GetTimestamp();
        if (
            (now - _lastMoveDiagnosticAt) / (double)Stopwatch.Frequency
            >= 0.1
        )
        {
            _lastMoveDiagnosticAt = now;
            DiagnosticLog(
                $"MOVE delta={deltaX},{deltaY} depth={preview.Depth} scale={preview.MapScale:F2} position={preview.ActualCursor.X},{preview.ActualCursor.Y}");
        }
    }

    private void AdjustDepth(int wheelDelta, Point actualPosition)
    {
        if (_gestureSession is null || wheelDelta == 0)
        {
            return;
        }

        var interaction = wheelDelta > 0
            ? _gestureSession.ZoomIn(
                Volatile.Read(ref _inputSettings).MaximumZoomLevel,
                actualPosition)
            : _gestureSession.ZoomOut(
                actualPosition);
        var preview = interaction.Preview;
        DiagnosticLog(
            $"WHEEL delta={wheelDelta} depth={preview.Depth} scale={preview.MapScale:F2} position={preview.ActualCursor.X},{preview.ActualCursor.Y}");
        QueuePreview(preview);
        _scaleAnimationTimer?.Start();
    }

    private void ScaleAnimationTimerOnTick(
        object? sender,
        EventArgs e)
    {
        if (_gestureSession is null)
        {
            _scaleAnimationTimer?.Stop();
            return;
        }

        QueuePreview(_gestureSession.RefreshScale());
        if (!_gestureSession.IsScaleAnimating)
        {
            _scaleAnimationTimer?.Stop();
        }
    }

    private void QueuePreview(NineGridPreview preview)
    {
        if (_disposed)
        {
            return;
        }

        Volatile.Write(ref _queuedPreview, preview);
        if (Interlocked.Exchange(ref _previewRenderQueued, 1) != 0)
        {
            return;
        }

        _dispatcher.BeginInvoke(
            RenderQueuedPreview,
            DispatcherPriority.Render);
    }

    private void RenderQueuedPreview()
    {
        if (Volatile.Read(ref _queuedPreview) is null)
        {
            Interlocked.Exchange(ref _previewRenderQueued, 0);
            return;
        }

        var now = Stopwatch.GetTimestamp();
        if (_lastPreviewRenderedAt != 0)
        {
            var elapsed = (now - _lastPreviewRenderedAt)
                / (double)Stopwatch.Frequency;
            var remaining = PreviewFrameIntervalSeconds - elapsed;
            if (remaining > 0)
            {
                _previewDelayTimer.Stop();
                _previewDelayTimer.Interval = TimeSpan.FromSeconds(
                    Math.Max(remaining, 0.001));
                _previewDelayTimer.Start();
                return;
            }
        }

        _previewDelayTimer.Stop();
        var queued = Interlocked.Exchange(ref _queuedPreview, null);
        if (!_disposed && JumpActive && queued is not null)
        {
            _overlayMap.ShowPreview(queued);
            _lastPreviewRenderedAt = Stopwatch.GetTimestamp();
        }

        Interlocked.Exchange(ref _previewRenderQueued, 0);
        if (
            Volatile.Read(ref _queuedPreview) is not null
            && Interlocked.Exchange(ref _previewRenderQueued, 1) == 0
        )
        {
            _dispatcher.BeginInvoke(
                RenderQueuedPreview,
                DispatcherPriority.Render);
        }
    }

    private void PreviewDelayTimerOnTick(object? sender, EventArgs e)
    {
        _previewDelayTimer.Stop();
        RenderQueuedPreview();
    }

    private void ConfirmGesture()
    {
        if (_gestureSession is null)
        {
            return;
        }

        var preview = _gestureSession.Preview;
        var destination = _gestureSession.CurrentPosition;
        DiagnosticLog(
            $"KEEP screen={preview.Display.Number} depth={preview.Depth} destination={destination.X},{destination.Y}");
        EndGesture();
        QueueCompletion($"KEEP  ·  LEVEL {preview.Depth}");
    }

    private void CancelGesture(bool restoreOrigin, bool showFeedback)
    {
        if (_gestureSession is null)
        {
            return;
        }

        var origin = _gestureSession.Origin;
        DiagnosticLog($"CANCEL restore={restoreOrigin} origin={origin.X},{origin.Y}");
        EndGesture();
        if (restoreOrigin)
        {
            _mouseMoveInjector.Queue(origin);
        }
        if (showFeedback)
        {
            QueueCompletion("CANCELLED", HudTone.Inactive);
        }
    }

    private void EndGesture()
    {
        _gestureSession = null;
        _screenJumpReleaseKey = null;
        _scaleAnimationTimer?.Stop();
        Volatile.Write(ref _jumpActive, 0);
        Interlocked.Exchange(ref _queuedPreview, null);
        _dispatcher.BeginInvoke(
            _overlayMap.HideAll,
            DispatcherPriority.Render);
        NotifyJumpStateChanged(false);
    }

    private void QueueCompletion(string message, HudTone tone = HudTone.Active)
    {
        if (_disposed)
        {
            return;
        }

        _dispatcher.BeginInvoke(
            () =>
            {
                if (!_disposed)
                {
                    ShowCompletion(message, tone);
                }
            },
            DispatcherPriority.Background);
    }

    private void ShowCompletion(string message, HudTone tone)
    {
        if (!_settings.ShowStatusHud)
        {
            return;
        }

        _hud ??= new ModeHudWindow();
        _hud.ShowMessage(message, tone, hideAfterMilliseconds: 800);
    }

    private void NotifyJumpStateChanged(bool active)
    {
        if (_disposed)
        {
            return;
        }

        _dispatcher.BeginInvoke(
            () =>
            {
                if (!_disposed)
                {
                    JumpStateChanged?.Invoke(active);
                }
            },
            DispatcherPriority.Background);
    }

    private void SettingsOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        var inputSettings = InputSettings.From(_settings);
        Volatile.Write(ref _inputSettings, inputSettings);
        var propertyName = e.PropertyName;

        if (propertyName == nameof(AppSettings.ShowStatusHud))
        {
            if (!_settings.ShowStatusHud)
            {
                _hud?.Hide();
            }
            return;
        }

        if (
            propertyName == nameof(AppSettings.Enabled)
            || propertyName == nameof(AppSettings.PositionMarksEnabled)
            || propertyName == nameof(AppSettings.ScreenJumpSequence)
            || propertyName == nameof(AppSettings.SavePositionSequence)
            || propertyName == nameof(AppSettings.RecallPositionSequence)
            || propertyName == nameof(AppSettings.SequenceTimeoutSeconds)
        )
        {
            _hookHost.Post(
                () =>
                {
                    _screenJumpMatcher.Reset();
                    ResetPositionShortcuts();
                    if (!inputSettings.Enabled
                        || propertyName != nameof(AppSettings.Enabled))
                    {
                        CancelGesture(
                            restoreOrigin: true,
                            showFeedback: false);
                    }
                },
                DispatcherPriority.Send);
        }
    }

    private void OverlayMapOnDisplaysChanged(
        IReadOnlyList<DisplayMonitor> displays)
    {
        _mouseMoveInjector.RefreshVirtualDesktopMetrics();
        Volatile.Write(ref _displaySnapshot, displays.ToArray());
    }

    private void OverlayMapOnDisplaysInvalidated()
    {
        _hookHost.Post(
            () => CancelGesture(
                restoreOrigin: false,
                showFeedback: false),
            DispatcherPriority.Send);
    }

    [Conditional("DEBUG")]
    private static void DiagnosticLog(string message)
    {
        Debug.WriteLine(message, "PrecisionJump.Input");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _previewDelayTimer.Stop();
        _previewDelayTimer.Tick -= PreviewDelayTimerOnTick;
        _settings.PropertyChanged -= SettingsOnPropertyChanged;
        _overlayMap.DisplaysInvalidated -= OverlayMapOnDisplaysInvalidated;
        _overlayMap.DisplaysChanged -= OverlayMapOnDisplaysChanged;
        Volatile.Write(ref _jumpActive, 0);
        _hookHost.Dispose();
        _mouseMoveInjector.Dispose();

        _overlayMap.Dispose();
        _mouseEventRecorder.Dispose();
        _hud?.Close();
    }

}
