using System.Drawing;
using System.Runtime.InteropServices;

namespace MouseAccelerator.Services;

internal sealed class MouseMoveInjector : IDisposable
{
    private sealed record Request(Point Target);

    private readonly AutoResetEvent _workAvailable = new(false);
    private readonly Thread _thread;
    private Request? _pending;
    private long _appliedPosition;
    private int _hasAppliedPosition;
    private int _injecting;
    private int _stopping;

    internal MouseMoveInjector()
    {
        _thread = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "PrecisionJump.MouseInjector"
        };
        _thread.Start();
    }

    internal void Queue(Point target)
    {
        if (Volatile.Read(ref _stopping) != 0)
        {
            return;
        }

        Interlocked.Exchange(ref _pending, new Request(target));
        _workAvailable.Set();
    }

    internal bool IsInjecting => Volatile.Read(ref _injecting) != 0;

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

            var request = Interlocked.Exchange(ref _pending, null);
            if (request is not null)
            {
                Volatile.Write(ref _injecting, 1);
                try
                {
                    SendAbsoluteMouseMove(request.Target);
                    ObservePosition(request.Target);
                }
                finally
                {
                    Volatile.Write(ref _injecting, 0);
                }
            }
        }
    }

    private static void SendAbsoluteMouseMove(Point target)
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
                        | NativeMethods.MouseEventVirtualDesk,
                    ExtraInfo = NativeMethods.MouseInjectionMarker
                }
            }
        };

        if (NativeMethods.SendInput(
            1,
            [input],
            Marshal.SizeOf<NativeMethods.Input>()) == 0)
        {
            NativeMethods.SetCursorPos(target.X, target.Y);
        }
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

        Interlocked.Exchange(ref _pending, null);
        _workAvailable.Set();
        if (_thread.Join(TimeSpan.FromMilliseconds(500)))
        {
            _workAvailable.Dispose();
        }
    }
}
