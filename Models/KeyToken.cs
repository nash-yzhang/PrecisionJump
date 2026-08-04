using System.Windows.Input;

namespace MouseAccelerator.Models;

public sealed record KeyToken(int VirtualKey)
{
    public string DisplayName => KeyNames.DisplayName(VirtualKey);
}

public static class KeyNames
{
    public const int Shift = 0x10;
    public const int Control = 0x11;
    public const int Alt = 0x12;
    public const int LeftWindows = 0x5B;
    public const int Backtick = 0xC0;

    public static int Normalize(int virtualKey) => virtualKey switch
    {
        0xA0 or 0xA1 => Shift,
        0xA2 or 0xA3 => Control,
        0xA4 or 0xA5 => Alt,
        0x5C => LeftWindows,
        _ => virtualKey
    };

    public static string DisplayName(int virtualKey)
    {
        virtualKey = Normalize(virtualKey);
        return virtualKey switch
        {
            Shift => "Shift",
            Control => "Ctrl",
            Alt => "Alt",
            LeftWindows => "Win",
            Backtick => "`",
            0x1B => "Esc",
            0x20 => "Space",
            0x09 => "Tab",
            0x0D => "Enter",
            0x08 => "Backspace",
            0x25 => "←",
            0x26 => "↑",
            0x27 => "→",
            0x28 => "↓",
            >= 0x30 and <= 0x39 => ((char)virtualKey).ToString(),
            >= 0x41 and <= 0x5A => ((char)virtualKey).ToString(),
            _ => KeyInterop.KeyFromVirtualKey(virtualKey).ToString()
        };
    }
}
