using System.Globalization;
using System.Text;
using MacroFenetre.Models;

namespace MacroFenetre.Services;

internal static class KeySequenceParser
{
    private static readonly Dictionary<string, (int VirtualKey, string DisplayName)> NamedKeys =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["ENTREE"] = (0x0D, "Entrée"),
            ["ENTER"] = (0x0D, "Entrée"),
            ["RETOUR"] = (0x0D, "Entrée"),
            ["TAB"] = (0x09, "Tab"),
            ["TABULATION"] = (0x09, "Tab"),
            ["ESPACE"] = (0x20, "Espace"),
            ["SPACE"] = (0x20, "Espace"),
            ["ECHAP"] = (0x1B, "Échap"),
            ["ESC"] = (0x1B, "Échap"),
            ["ESCAPE"] = (0x1B, "Échap"),
            ["RETOUR ARRIERE"] = (0x08, "Retour arrière"),
            ["BACKSPACE"] = (0x08, "Retour arrière"),
            ["SUPPR"] = (0x2E, "Suppr"),
            ["DELETE"] = (0x2E, "Suppr"),
            ["INSER"] = (0x2D, "Inser"),
            ["INSERT"] = (0x2D, "Inser"),
            ["GAUCHE"] = (0x25, "Gauche"),
            ["LEFT"] = (0x25, "Gauche"),
            ["HAUT"] = (0x26, "Haut"),
            ["UP"] = (0x26, "Haut"),
            ["DROITE"] = (0x27, "Droite"),
            ["RIGHT"] = (0x27, "Droite"),
            ["BAS"] = (0x28, "Bas"),
            ["DOWN"] = (0x28, "Bas"),
            ["DEBUT"] = (0x24, "Début"),
            ["HOME"] = (0x24, "Début"),
            ["FIN"] = (0x23, "Fin"),
            ["END"] = (0x23, "Fin"),
            ["PAGE PRECEDENTE"] = (0x21, "Page précédente"),
            ["PAGE UP"] = (0x21, "Page précédente"),
            ["PAGE SUIVANTE"] = (0x22, "Page suivante"),
            ["PAGE DOWN"] = (0x22, "Page suivante")
        };

    internal static bool TryParse(
        string text,
        out IReadOnlyList<KeyChoice> actions,
        out string error)
    {
        var parsedActions = new List<KeyChoice>();
        var tokens = text.Split([',', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        if (tokens.Length == 0)
        {
            actions = [];
            error = "Ajoutez au moins une action, par exemple : T, Ctrl+V, Entrée.";
            return false;
        }

        foreach (var token in tokens)
        {
            if (!TryParseAction(token, out var action, out error))
            {
                actions = [];
                return false;
            }

            parsedActions.Add(action);
        }

        actions = parsedActions;
        error = string.Empty;
        return true;
    }

    private static bool TryParseAction(string token, out KeyChoice action, out string error)
    {
        var parts = token.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var modifiers = HotkeyModifiers.None;
        string? keyPart = null;

        foreach (var part in parts)
        {
            switch (Normalize(part))
            {
                case "CTRL":
                case "CONTROL":
                    modifiers |= HotkeyModifiers.Control;
                    break;
                case "ALT":
                    modifiers |= HotkeyModifiers.Alt;
                    break;
                case "MAJ":
                case "SHIFT":
                    modifiers |= HotkeyModifiers.Shift;
                    break;
                case "WIN":
                case "WINDOWS":
                    modifiers |= HotkeyModifiers.Windows;
                    break;
                default:
                    if (keyPart is not null)
                    {
                        action = KeyChoice.F6;
                        error = $"Action non reconnue : « {token} ». Séparez les actions avec une virgule.";
                        return false;
                    }

                    keyPart = part;
                    break;
            }
        }

        if (keyPart is null || !TryResolveKey(keyPart, out var virtualKey, out var displayName))
        {
            action = KeyChoice.F6;
            error = $"Touche non reconnue dans « {token} ».";
            return false;
        }

        var modifierText = FormatModifiers(modifiers);
        action = new KeyChoice($"{modifierText}{displayName}", virtualKey, modifiers);
        error = string.Empty;
        return true;
    }

    private static bool TryResolveKey(string text, out int virtualKey, out string displayName)
    {
        var normalized = Normalize(text);
        if (normalized.Length == 1 && normalized[0] is >= 'A' and <= 'Z')
        {
            virtualKey = normalized[0];
            displayName = normalized;
            return true;
        }

        if (normalized.Length == 1 && normalized[0] is >= '0' and <= '9')
        {
            virtualKey = normalized[0];
            displayName = normalized;
            return true;
        }

        if (normalized.StartsWith('F') &&
            int.TryParse(normalized.AsSpan(1), out var functionNumber) &&
            functionNumber is >= 1 and <= 24)
        {
            virtualKey = 0x6F + functionNumber;
            displayName = $"F{functionNumber}";
            return true;
        }

        if (normalized.StartsWith("PAVE ") &&
            int.TryParse(normalized.AsSpan(5), out var numpadNumber) &&
            numpadNumber is >= 0 and <= 9)
        {
            virtualKey = 0x60 + numpadNumber;
            displayName = $"Pavé {numpadNumber}";
            return true;
        }

        if (NamedKeys.TryGetValue(normalized, out var namedKey))
        {
            virtualKey = namedKey.VirtualKey;
            displayName = namedKey.DisplayName;
            return true;
        }

        virtualKey = 0;
        displayName = string.Empty;
        return false;
    }

    private static string FormatModifiers(HotkeyModifiers modifiers)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(HotkeyModifiers.Control))
        {
            parts.Add("Ctrl");
        }

        if (modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            parts.Add("Alt");
        }

        if (modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            parts.Add("Maj");
        }

        if (modifiers.HasFlag(HotkeyModifiers.Windows))
        {
            parts.Add("Windows");
        }

        return parts.Count == 0 ? string.Empty : string.Join(" + ", parts) + " + ";
    }

    private static string Normalize(string value)
    {
        var decomposed = value.Trim().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(char.ToUpperInvariant(character));
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }
}
