using System.Drawing;

namespace PrecisionJump.Services;

internal static class MousePositionRegisters
{
    internal static bool TryGetRegister(int virtualKey, out char register)
    {
        if (virtualKey is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A)
        {
            register = (char)virtualKey;
            return true;
        }

        if (virtualKey is >= 0x60 and <= 0x69)
        {
            register = (char)('0' + virtualKey - 0x60);
            return true;
        }

        register = default;
        return false;
    }

    internal static Point ResolveDestination(
        Point savedPosition,
        IReadOnlyList<DisplayMonitor> displays)
    {
        if (displays.Count == 0
            || displays.Any(display => display.Bounds.Contains(savedPosition)))
        {
            return savedPosition;
        }

        var nearest = savedPosition;
        var nearestDistance = long.MaxValue;
        foreach (var display in displays)
        {
            var bounds = display.Bounds;
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                continue;
            }

            var candidate = new Point(
                Math.Clamp(savedPosition.X, bounds.Left, bounds.Right - 1),
                Math.Clamp(savedPosition.Y, bounds.Top, bounds.Bottom - 1));
            var deltaX = (long)candidate.X - savedPosition.X;
            var deltaY = (long)candidate.Y - savedPosition.Y;
            var distance = deltaX * deltaX + deltaY * deltaY;
            if (distance < nearestDistance)
            {
                nearest = candidate;
                nearestDistance = distance;
            }
        }

        return nearest;
    }
}
