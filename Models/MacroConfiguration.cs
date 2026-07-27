namespace MacroFenetre.Models;

public sealed class MacroConfiguration
{
    public const int CurrentFormatVersion = 3;

    public int FormatVersion { get; set; } = CurrentFormatVersion;
    public KeyConfiguration SwitchKey { get; set; } = KeyConfiguration.From(KeyChoice.F8);
    public List<WindowConfiguration> SelectedWindows { get; set; } = [];
    public List<ClickMacroConfiguration> Macros { get; set; } = [];
    public List<ActionSequenceConfiguration> ActionSequences { get; set; } = [];
    public int ActionSequenceDelayMs { get; set; } = 150;
    public int StabilizationDelayMs { get; set; } = 260;
    public bool RestoreCursor { get; set; } = true;
    public bool ShortcutsEnabled { get; set; } = true;
}

public sealed class ActionSequenceConfiguration
{
    public InputTriggerConfiguration Trigger { get; set; } = new();
    public List<KeyConfiguration> Actions { get; set; } = [];
    public string WindowTitle { get; set; } = string.Empty;
    public string ProcessName { get; set; } = string.Empty;
}

public sealed class InputTriggerConfiguration
{
    public string Name { get; set; } = string.Empty;
    public InputTriggerKind Kind { get; set; }
    public int Code { get; set; }
    public HotkeyModifiers Modifiers { get; set; }

    public static InputTriggerConfiguration From(InputTrigger trigger) => new()
    {
        Name = trigger.Name,
        Kind = trigger.Kind,
        Code = trigger.Code,
        Modifiers = trigger.Modifiers
    };

    public InputTrigger ToInputTrigger() =>
        new(Name, Kind, Code, Modifiers);
}

public sealed class KeyConfiguration
{
    public string Name { get; set; } = string.Empty;
    public int VirtualKey { get; set; }
    public HotkeyModifiers Modifiers { get; set; }

    public static KeyConfiguration From(KeyChoice key) => new()
    {
        Name = key.Name,
        VirtualKey = key.VirtualKey,
        Modifiers = key.Modifiers
    };

    public KeyChoice ToKeyChoice(KeyChoice fallback) =>
        VirtualKey == 0 ? fallback : new KeyChoice(string.IsNullOrWhiteSpace(Name) ? fallback.Name : Name, VirtualKey, Modifiers);
}

public sealed class WindowConfiguration
{
    public string ProcessName { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
}

public sealed class ClickMacroConfiguration
{
    public KeyConfiguration Key { get; set; } = new();
    public string WindowTitle { get; set; } = string.Empty;
    public string ProcessName { get; set; } = string.Empty;
    public double RelativeX { get; set; }
    public double RelativeY { get; set; }
    public int ClientX { get; set; }
    public int ClientY { get; set; }
    public int ReferenceWidth { get; set; }
    public int ReferenceHeight { get; set; }
    public bool ApplyToMatchingWindows { get; set; }
}
