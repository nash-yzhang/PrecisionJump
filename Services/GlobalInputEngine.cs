using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using MouseAccelerator.Models;
using MouseAccelerator.Views;

namespace MouseAccelerator.Services;

public sealed class GlobalInputEngine : IDisposable
{
    private readonly AppSettings _settings;
    private readonly Dispatcher _dispatcher;
    private readonly NativeMethods.HookProc _keyboardCallback;
    private readonly NativeMethods.HookProc _mouseCallback;
    private readonly SequenceMatcher _screenJumpMatcher = new();
    private readonly HashSet<int> _heldKeys = [];
    private readonly HashSet<int> _suppressedShortcutKeys = [];
    private readonly ScreenOverlayMap _overlayMap;
    private readonly MouseEventRecorder _mouseEventRecorder;
    private readonly DispatcherTimer _scaleAnimationTimer;

    private nint _keyboardHook;
    private nint _mouseHook;
    private NineGridSession? _gestureSession;
    private int? _screenJumpReleaseKey;
    private bool _suppressLeftButtonUp;
    private bool _suppressRightButtonUp;
    private bool _suppressMiddleButtonUp;
    private bool _suppressXButtonUp;
    private NineGridPreview? _queuedPreview;
    private bool _previewRenderQueued;
    private long _lastMoveDiagnosticAt;
    private Point? _lastObservedCursorPosition;
    private ModeHudWindow? _hud;
    private bool _disposed;

    public GlobalInputEngine(AppSettings settings, Dispatcher dispatcher)
    {
        _settings = settings;
        _dispatcher = dispatcher;
        _keyboardCallback = KeyboardHookCallback;
        _mouseCallback = MouseHookCallback;
        _overlayMap = new ScreenOverlayMap(dispatcher);
        _mouseEventRecorder = new MouseEventRecorder();
        _scaleAnimationTimer = new DispatcherTimer(
            DispatcherPriority.Render,
            dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _scaleAnimationTimer.Tick += ScaleAnimationTimerOnTick;
        _settings.PropertyChanged += SettingsOnPropertyChanged;
    }

    public event Action<bool>? JumpStateChanged;

    public bool JumpActive => _gestureSession is not null;
    public bool IsCapturingKey { get; set; }

    public void Start()
    {
        if (_keyboardHook != 0)
        {
            return;
        }

        DiagnosticLog("ENGINE STARTING");
        var module = NativeMethods.GetModuleHandle(null);
        _keyboardHook = NativeMethods.SetWindowsHookEx(
            NativeMethods.WhKeyboardLl,
            _keyboardCallback,
            module,
            0);
        if (_keyboardHook == 0)
        {
            throw new InvalidOperationException(
                $"Could not install the keyboard hook (Win32 error {Marshal.GetLastWin32Error()}).");
        }

        _mouseHook = NativeMethods.SetWindowsHookEx(
            NativeMethods.WhMouseLl,
            _mouseCallback,
            module,
            0);
        if (_mouseHook == 0)
        {
            NativeMethods.UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = 0;
            throw new InvalidOperationException(
                $"Could not install the mouse hook (Win32 error {Marshal.GetLastWin32Error()}).");
        }
        DiagnosticLog("ENGINE STARTED");
    }

    public void PrepareForShortcutCapture()
    {
        CancelGesture(restoreOrigin: true, showFeedback: false);
        _screenJumpMatcher.Reset();
        _suppressedShortcutKeys.Clear();
    }

    private nint KeyboardHookCallback(int code, nint wParam, nint lParam)
    {
        if (code < 0)
        {
            return NativeMethods.CallNextHookEx(_keyboardHook, code, wParam, lParam);
        }

        try
        {
            var message = wParam.ToInt32();
            var isDown = message is NativeMethods.WmKeyDown or NativeMethods.WmSysKeyDown;
            var isUp = message is NativeMethods.WmKeyUp or NativeMethods.WmSysKeyUp;
            if (!isDown && !isUp)
            {
                return NativeMethods.CallNextHookEx(_keyboardHook, code, wParam, lParam);
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
            // Input hooks always fail open.
        }

        return NativeMethods.CallNextHookEx(_keyboardHook, code, wParam, lParam);
    }

    private nint MouseHookCallback(int code, nint wParam, nint lParam)
    {
        if (code < 0)
        {
            return NativeMethods.CallNextHookEx(_mouseHook, code, wParam, lParam);
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
                                _mouseHook,
                                code,
                                wParam,
                                lParam);
                        }

                        var position = hookPosition;
                        var current = _gestureSession.CurrentPosition;
                        var rawDeltaX = position.X - current.X;
                        var rawDeltaY = position.Y - current.Y;
                        var interaction = _gestureSession.MoveContinuous(
                            rawDeltaX,
                            rawDeltaY,
                            _settings.SelectionDistance);
                        if (interaction.JumpTarget is Point target)
                        {
                            SendAbsoluteMouseMove(target);
                            _lastObservedCursorPosition = target;
                            RecordMouseEvent(
                                target,
                                "move",
                                jumping: true,
                                rawDeltaX,
                                rawDeltaY);
                        }
                        QueuePreview(interaction.Preview);
                        LogContinuousMove(
                            position.X - current.X,
                            position.Y - current.Y,
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
            // Mouse input remains available if preview rendering fails.
        }

        return NativeMethods.CallNextHookEx(_mouseHook, code, wParam, lParam);
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
        if (_settings.RecordMouseEvents)
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

        if (!firstKeyDown || !_settings.Enabled)
        {
            return false;
        }

        var progress = _screenJumpMatcher.Consume(
            virtualKey,
            _settings.ScreenJumpSequence
                .Select(token => token.VirtualKey)
                .ToList(),
            TimeSpan.FromSeconds(_settings.SequenceTimeoutSeconds));
        if (progress is MatchProgress.None or MatchProgress.Prefix)
        {
            return false;
        }

        DiagnosticLog($"SEQUENCE MATCH final={virtualKey}");
        BeginGesture(finalKey: virtualKey);
        var suppressActivation =
            ShortcutPolicy.ShouldSuppressActivationKey(virtualKey);
        if (suppressActivation)
        {
            _suppressedShortcutKeys.Add(virtualKey);
        }
        return suppressActivation;
    }

    private void BeginGesture(int finalKey)
    {
        if (!NativeMethods.GetCursorPos(out var cursor))
        {
            return;
        }

        var displays = _overlayMap.Displays;
        if (displays.Count == 0)
        {
            return;
        }

        _gestureSession = new NineGridSession(
            new Point(cursor.X, cursor.Y),
            displays);
        _lastObservedCursorPosition =
            new Point(cursor.X, cursor.Y);
        _screenJumpReleaseKey = finalKey;
        DiagnosticLog(
            $"BEGIN origin={cursor.X},{cursor.Y} release={finalKey} displays={displays.Count}");
        QueuePreview(_gestureSession.Preview);
        JumpStateChanged?.Invoke(true);
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
                _settings.MaximumZoomLevel,
                actualPosition)
            : _gestureSession.ZoomOut(
                actualPosition);
        var preview = interaction.Preview;
        DiagnosticLog(
            $"WHEEL delta={wheelDelta} depth={preview.Depth} scale={preview.MapScale:F2} position={preview.ActualCursor.X},{preview.ActualCursor.Y}");
        QueuePreview(preview);
        _scaleAnimationTimer.Start();
    }

    private void ScaleAnimationTimerOnTick(
        object? sender,
        EventArgs e)
    {
        if (_gestureSession is null)
        {
            _scaleAnimationTimer.Stop();
            return;
        }

        QueuePreview(_gestureSession.RefreshScale());
        if (!_gestureSession.IsScaleAnimating)
        {
            _scaleAnimationTimer.Stop();
        }
    }

    private void QueuePreview(NineGridPreview preview)
    {
        _queuedPreview = preview;
        if (_previewRenderQueued)
        {
            return;
        }

        _previewRenderQueued = true;
        _dispatcher.BeginInvoke(
            () =>
            {
                _previewRenderQueued = false;
                var queued = _queuedPreview;
                if (_gestureSession is null || queued is null)
                {
                    return;
                }

                _overlayMap.ShowPreview(queued);
            },
            DispatcherPriority.Render);
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
        ShowCompletion($"KEEP  ·  LEVEL {preview.Depth}");
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
            SendAbsoluteMouseMove(origin);
        }
        if (showFeedback)
        {
            ShowCompletion("CANCELLED", HudTone.Inactive);
        }
    }

    private void EndGesture()
    {
        _gestureSession = null;
        _screenJumpReleaseKey = null;
        _scaleAnimationTimer.Stop();
        _queuedPreview = null;
        _dispatcher.BeginInvoke(
            _overlayMap.HideAll,
            DispatcherPriority.Render);
        JumpStateChanged?.Invoke(false);
    }

    private void ShowCompletion(string message, HudTone tone = HudTone.Active)
    {
        if (!_settings.ShowStatusHud)
        {
            return;
        }

        _hud ??= new ModeHudWindow();
        _hud.ShowMessage(message, tone, hideAfterMilliseconds: 800);
    }

    private void SendAbsoluteMouseMove(Point target)
    {
        var left = NativeMethods.GetSystemMetrics(NativeMethods.SmXVirtualScreen);
        var top = NativeMethods.GetSystemMetrics(NativeMethods.SmYVirtualScreen);
        var width = Math.Max(
            NativeMethods.GetSystemMetrics(NativeMethods.SmCxVirtualScreen),
            2);
        var height = Math.Max(
            NativeMethods.GetSystemMetrics(NativeMethods.SmCyVirtualScreen),
            2);

        var normalizedX = (int)Math.Round(
            (target.X - left) * 65_535d / (width - 1));
        var normalizedY = (int)Math.Round(
            (target.Y - top) * 65_535d / (height - 1));
        var input = new NativeMethods.Input
        {
            Type = NativeMethods.InputMouse,
            Data = new NativeMethods.InputUnion
            {
                Mouse = new NativeMethods.MouseInput
                {
                    X = Math.Clamp(normalizedX, 0, 65_535),
                    Y = Math.Clamp(normalizedY, 0, 65_535),
                    Flags = NativeMethods.MouseEventMove
                        | NativeMethods.MouseEventMoveNoCoalesce
                        | NativeMethods.MouseEventAbsolute
                        | NativeMethods.MouseEventVirtualDesk
                }
            }
        };

        var sent = NativeMethods.SendInput(
            1,
            [input],
            Marshal.SizeOf<NativeMethods.Input>());
        DiagnosticLog(
            $"SEND_INPUT sent={sent} target={target.X},{target.Y} error={Marshal.GetLastWin32Error()}");
        if (sent == 0)
        {
            _dispatcher.BeginInvoke(
                () => NativeMethods.SetCursorPos(target.X, target.Y),
                DispatcherPriority.Input);
        }
    }

    private void SettingsOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppSettings.Enabled):
                if (!_settings.Enabled)
                {
                    CancelGesture(restoreOrigin: true, showFeedback: false);
                }
                break;
            case nameof(AppSettings.ScreenJumpSequence):
            case nameof(AppSettings.SequenceTimeoutSeconds):
                _screenJumpMatcher.Reset();
                CancelGesture(restoreOrigin: true, showFeedback: false);
                break;
            case nameof(AppSettings.ShowStatusHud):
                if (!_settings.ShowStatusHud)
                {
                    _hud?.Hide();
                }
                break;
        }
    }

    private static void DiagnosticLog(string message)
    {
        var path = Environment.GetEnvironmentVariable(
            "MOUSE_ACCELERATOR_DIAGNOSTIC_LOG");
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            File.AppendAllText(
                path,
                $"{DateTime.UtcNow:O} {message}{Environment.NewLine}");
        }
        catch
        {
            // Diagnostics must never interfere with input.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _settings.PropertyChanged -= SettingsOnPropertyChanged;
        CancelGesture(restoreOrigin: true, showFeedback: false);

        if (_keyboardHook != 0)
        {
            NativeMethods.UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = 0;
        }
        if (_mouseHook != 0)
        {
            NativeMethods.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = 0;
        }

        _overlayMap.Dispose();
        _mouseEventRecorder.Dispose();
        _hud?.Close();
    }

}
