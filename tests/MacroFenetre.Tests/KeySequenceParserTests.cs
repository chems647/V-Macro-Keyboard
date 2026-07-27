using MacroFenetre.Models;
using MacroFenetre.Services;

namespace MacroFenetre.Tests;

public sealed class KeySequenceParserTests
{
    [Fact]
    public void ParseBuildsExpectedKeyboardSequence()
    {
        var parsed = KeySequenceParser.TryParse(
            "T, Ctrl+V, Entrée",
            out var actions,
            out var error);

        Assert.True(parsed, error);
        Assert.Collection(
            actions,
            action => Assert.Equal(new KeyChoice("T", 0x54), action),
            action => Assert.Equal(new KeyChoice("Ctrl + V", 0x56, HotkeyModifiers.Control), action),
            action => Assert.Equal(new KeyChoice("Entrée", 0x0D), action));
    }

    [Theory]
    [InlineData("Maj+F5; Alt+Tab; Pavé 3", 3)]
    [InlineData("Gauche, Haut, Droite, Bas", 4)]
    [InlineData("Windows+R", 1)]
    public void ParseAcceptsSupportedAliases(string text, int expectedCount)
    {
        var parsed = KeySequenceParser.TryParse(text, out var actions, out var error);

        Assert.True(parsed, error);
        Assert.Equal(expectedCount, actions.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl+")]
    [InlineData("Touche inconnue")]
    [InlineData("Ctrl+V+T")]
    public void ParseRejectsInvalidActions(string text)
    {
        Assert.False(KeySequenceParser.TryParse(text, out _, out var error));
        Assert.NotEmpty(error);
    }
}
