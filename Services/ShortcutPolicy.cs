namespace MouseAccelerator.Services;

internal static class ShortcutPolicy
{
    private const int Shift = 0x10;
    private const int Control = 0x11;
    private const int Alt = 0x12;
    private const int LeftWindows = 0x5B;

    internal static bool ShouldSuppressActivationKey(int virtualKey)
    {
        return virtualKey is not (Shift or Control or Alt or LeftWindows);
    }

    internal static bool ShouldCancelActiveGesture(
        int? finalKey,
        int pressedKey,
        bool firstKeyDown)
    {
        return firstKeyDown && pressedKey != finalKey;
    }
}
