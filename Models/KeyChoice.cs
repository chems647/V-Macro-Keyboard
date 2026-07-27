namespace MacroFenetre.Models;

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Control = 1,
    Alt = 2,
    Shift = 4,
    Windows = 8
}

public sealed record KeyChoice(string Name, int VirtualKey, HotkeyModifiers Modifiers = HotkeyModifiers.None)
{
    public static KeyChoice F6 { get; } = new("F6", 0x75);
    public static KeyChoice F8 { get; } = new("F8", 0x77);

    public bool Matches(int virtualKey, HotkeyModifiers modifiers) =>
        VirtualKey == virtualKey && Modifiers == modifiers;
}
