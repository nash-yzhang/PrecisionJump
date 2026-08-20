using System.Drawing;
using System.Runtime.InteropServices;

namespace PrecisionJump.Services;

internal sealed class MouseMoveInjector : IDisposable
{
    private static readonly int NativeInputSize =
        Marshal.SizeOf<NativeMethods.Input>();

    private sealed record VirtualDesktopMetrics(
        int Left,
        int Top,
        int Width,
        int Height);

    private readonly AutoResetEvent _workAvailable = new(false);
    private readonly Thread _thread;
    private VirtualDesktopMetrics _virtualDesktopMetrics;
    private long _pendingPosition;
    private long _appliedPosition;
    private int _hasPendingPosition;
    private int _hasAppliedPosition;
    private int _stopping;

    internal MouseMoveInjector()
    {
        _virtualDesktopMetrics = ReadVirtualDesktopMetrics();
        _thread = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "PrecisionJump.MouseInjector",
            Priority = ThreadPriority.AboveNormal
        };
        _thread.Start();
    }

    internal void Queue(Point target)
    {
        if (Volatile.Read(ref _stopping) != 0)
        {
            return;
        }

        Interlocked.Exchange(ref _pendingPosition, Pack(target));
        Volatile.Write(ref _hasPendingPosition, 1);
        _workAvailable.Set();
    }

    internal void RefreshVirtualDesktopMetrics()
    {
        Volatile.Write(
            ref _virtualDesktopMetrics,
            ReadVirtualDesktopMetrics());
    }

    internal Point AppliedPosition
    {
        get
        {
            if (Volatile.Read(ref _hasAppliedPosition) == 0)
            {
                throw new InvalidOperationException(
                    "The mouse injection position has not been initialized.");
            }

            return Unpack(Interlocked.Read(ref _appliedPosition));
        }
    }

    internal void ObservePosition(Point position)
    {
        Interlocked.Exchange(ref _appliedPosition, Pack(position));
        Volatile.Write(ref _hasAppliedPosition, 1);
    }

    internal Point PhysicalDeltaFrom(Point observedPosition)
    {
        var appliedPosition = AppliedPosition;
        return new Point(
            observedPosition.X - appliedPosition.X,
            observedPosition.Y - appliedPosition.Y);
    }

    private void WorkerLoop()
    {
        while (Volatile.Read(ref _stopping) == 0)
        {
            _workAvailable.WaitOne();
            if (Volatile.Read(ref _stopping) != 0)
            {
                break;
            }

            while (Interlocked.Exchange(ref _hasPendingPosition, 0) != 0)
            {
                var target = Unpack(
                    Interlocked.Read(ref _pendingPosition));
                SendAbsoluteMouseMove(target);
                if (Volatile.Read(ref _stopping) != 0)
                {
                    break;
                }
            }
        }
    }

    private void SendAbsoluteMouseMove(Point target)
    {
        var metrics = Volatile.Read(ref _virtualDesktopMetrics);

        var normalizedX = (int)Math.Round(
            (target.X - metrics.Left) * 65_535d / (metrics.Width - 1));
        var normalizedY = (int)Math.Round(
            (target.Y - metrics.Top) * 65_535d / (metrics.Height - 1));
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
                        | NativeMethods.MouseEventAbsolute
                        | NativeMethods.MouseEventVirtualDesk,
                    ExtraInfo = NativeMethods.MouseInjectionMarker
                }
            }
        };

        if (NativeMethods.SendInput(
            1,
            ref input,
            NativeInputSize) == 0)
        {
            NativeMethods.SetCursorPos(target.X, target.Y);
        }
    }

    private static VirtualDesktopMetrics ReadVirtualDesktopMetrics()
    {
        return new VirtualDesktopMetrics(
            NativeMethods.GetSystemMetrics(NativeMethods.SmXVirtualScreen),
            NativeMethods.GetSystemMetrics(NativeMethods.SmYVirtualScreen),
            Math.Max(
                NativeMethods.GetSystemMetrics(NativeMethods.SmCxVirtualScreen),
                2),
            Math.Max(
                NativeMethods.GetSystemMetrics(NativeMethods.SmCyVirtualScreen),
                2));
    }

    private static long Pack(Point position)
    {
        return ((long)(uint)position.X << 32) | (uint)position.Y;
    }

    private static Point Unpack(long packed)
    {
        return new Point(
            unchecked((int)(packed >> 32)),
            unchecked((int)(uint)packed));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0)
        {
            return;
        }

        Volatile.Write(ref _hasPendingPosition, 0);
        _workAvailable.Set();
        if (_thread.Join(TimeSpan.FromMilliseconds(500)))
        {
            _workAvailable.Dispose();
        }
    }
}
