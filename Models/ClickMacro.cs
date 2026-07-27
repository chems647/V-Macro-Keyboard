namespace MacroFenetre.Models;

public sealed class ClickMacro
{
    public required int VirtualKey { get; init; }
    public required HotkeyModifiers Modifiers { get; init; }
    public required string KeyName { get; init; }
    public required nint WindowHandle { get; init; }
    public required string WindowTitle { get; init; }
    public required string ProcessName { get; init; }
    public required double RelativeX { get; init; }
    public required double RelativeY { get; init; }
    public int ClientX { get; init; }
    public int ClientY { get; init; }
    public int ReferenceWidth { get; init; }
    public int ReferenceHeight { get; init; }
    public required bool ApplyToMatchingWindows { get; init; }

    public string TargetDisplay => ApplyToMatchingWindows
        ? $"Toutes les fenêtres {ProcessName} sélectionnées"
        : $"{ProcessName} · {WindowTitle}";
    public string PositionDisplay => ReferenceWidth > 0 && ReferenceHeight > 0
        ? $"{ClientX} × {ClientY} px"
        : $"{RelativeX:P0} × {RelativeY:P0}";
}
