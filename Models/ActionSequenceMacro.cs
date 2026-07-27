namespace MacroFenetre.Models;

public enum InputTriggerKind
{
    Keyboard,
    Mouse
}

public sealed record InputTrigger(
    string Name,
    InputTriggerKind Kind,
    int Code,
    HotkeyModifiers Modifiers = HotkeyModifiers.None)
{
    public static InputTrigger Keyboard(KeyChoice key) =>
        new(key.Name, InputTriggerKind.Keyboard, key.VirtualKey, key.Modifiers);

    public bool MatchesKeyboard(int virtualKey, HotkeyModifiers modifiers) =>
        Kind == InputTriggerKind.Keyboard && Code == virtualKey && Modifiers == modifiers;

    public bool MatchesMouse(int buttonCode) =>
        Kind == InputTriggerKind.Mouse && Code == buttonCode;
}

public sealed class ActionSequenceMacro
{
    public required InputTrigger Trigger { get; init; }
    public required IReadOnlyList<KeyChoice> Actions { get; init; }
    public required string WindowTitle { get; init; }
    public required string ProcessName { get; init; }
    public nint WindowHandle { get; set; }

    public string TriggerDisplay => Trigger.Name;
    public string TargetDisplay => $"{ProcessName}  ·  {WindowTitle}";
    public string ActionsDisplay => string.Join("  →  ", Actions.Select(action => action.Name));
}
