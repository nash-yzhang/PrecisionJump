using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace PrecisionJump.Services;

internal sealed class LowLevelHookHost : IDisposable
{
    private readonly NativeMethods.HookProc _keyboardCallback;
    private readonly NativeMethods.HookProc _mouseCallback;
    private readonly ManualResetEventSlim _started = new(false);
    private readonly Thread _thread;

    private Dispatcher? _dispatcher;
    private Exception? _startException;
    private nint _keyboardHook;
    private nint _mouseHook;
    private volatile bool _disposed;

    internal LowLevelHookHost(
        NativeMethods.HookProc keyboardCallback,
        NativeMethods.HookProc mouseCallback)
    {
        _keyboardCallback = keyboardCallback;
        _mouseCallback = mouseCallback;
        _thread = new Thread(ThreadMain)
        {
            IsBackground = true,
            Name = "PrecisionJump.InputHooks",
            Priority = ThreadPriority.AboveNormal
        };
        _thread.SetApartmentState(ApartmentState.MTA);
    }

    internal Dispatcher Dispatcher => _dispatcher
        ?? throw new InvalidOperationException("The input hook thread is not running.");

    internal void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _thread.Start();
        if (!_started.Wait(TimeSpan.FromSeconds(5)))
        {
            throw new TimeoutException("The input hook thread did not start within five seconds.");
        }

        if (_startException is not null)
        {
            throw new InvalidOperationException(
                "The global input hooks could not be installed.",
                _startException);
        }
    }

    internal void Post(Action action, DispatcherPriority priority = DispatcherPriority.Normal)
    {
        if (_disposed || _dispatcher is null || _dispatcher.HasShutdownStarted)
        {
            return;
        }

        _dispatcher.BeginInvoke(action, priority);
    }

    private void ThreadMain()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        try
        {
            InstallHooks();
        }
        catch (Exception exception)
        {
            _startException = exception;
            UninstallHooks();
            _started.Set();
            return;
        }

        _started.Set();
        try
        {
            Dispatcher.Run();
        }
        finally
        {
            UninstallHooks();
        }
    }

    private void InstallHooks()
    {
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
            var error = Marshal.GetLastWin32Error();
            NativeMethods.UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = 0;
            throw new InvalidOperationException(
                $"Could not install the mouse hook (Win32 error {error}).");
        }
    }

    private void UninstallHooks()
    {
        var keyboardHook = Interlocked.Exchange(ref _keyboardHook, 0);
        if (keyboardHook != 0)
        {
            NativeMethods.UnhookWindowsHookEx(keyboardHook);
        }

        var mouseHook = Interlocked.Exchange(ref _mouseHook, 0);
        if (mouseHook != 0)
        {
            NativeMethods.UnhookWindowsHookEx(mouseHook);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_dispatcher is not null && !_dispatcher.HasShutdownStarted)
        {
            _dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
        }

        if (_thread.IsAlive && !_thread.Join(TimeSpan.FromSeconds(2)))
        {
            // Never let shutdown wait indefinitely on a system hook.
            UninstallHooks();
        }

        _started.Dispose();
    }
}
