using System.Text;
using MacroFenetre.Models;
using MacroFenetre.Services;

namespace MacroFenetre.Tests;

public sealed class ConfigurationServiceTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        $"MacroFenetre.Tests.{Guid.NewGuid():N}");

    public ConfigurationServiceTests()
    {
        Directory.CreateDirectory(_temporaryDirectory);
    }

    [Fact]
    public async Task SaveAndLoadPreserveActionSequences()
    {
        var path = GetPath("profile.json");
        var configuration = new MacroConfiguration
        {
            ActionSequenceDelayMs = 120,
            ActionSequences =
            [
                new ActionSequenceConfiguration
                {
                    Trigger = InputTriggerConfiguration.From(new InputTrigger(
                        "Bouton latéral 1",
                        InputTriggerKind.Mouse,
                        GlobalMouseHook.SideButton1)),
                    Actions =
                    [
                        KeyConfiguration.From(new KeyChoice("T", 0x54)),
                        KeyConfiguration.From(new KeyChoice("Entrée", 0x0D))
                    ]
                }
            ]
        };

        await ConfigurationService.SaveAsync(path, configuration);
        var loaded = await ConfigurationService.LoadAsync(path);

        Assert.Equal(MacroConfiguration.CurrentFormatVersion, loaded.FormatVersion);
        Assert.Equal(120, loaded.ActionSequenceDelayMs);
        var sequence = Assert.Single(loaded.ActionSequences);
        Assert.Equal(InputTriggerKind.Mouse, sequence.Trigger.Kind);
        Assert.Equal(GlobalMouseHook.SideButton1, sequence.Trigger.Code);
        Assert.Equal(2, sequence.Actions.Count);
    }

    [Fact]
    public async Task LoadUpgradesLegacyProfile()
    {
        var path = GetPath("legacy.json");
        await File.WriteAllTextAsync(
            path,
            """
            {
              "formatVersion": 1,
              "switchKey": { "name": "F8", "virtualKey": 119, "modifiers": "None" },
              "selectedWindows": [],
              "macros": []
            }
            """,
            Encoding.UTF8);

        var loaded = await ConfigurationService.LoadAsync(path);

        Assert.Equal(MacroConfiguration.CurrentFormatVersion, loaded.FormatVersion);
        Assert.Empty(loaded.ActionSequences);
        Assert.Equal(80, loaded.ActionSequenceDelayMs);
    }

    [Fact]
    public async Task LoadRejectsNewerProfileFormat()
    {
        var path = GetPath("future.json");
        await File.WriteAllTextAsync(
            path,
            """{ "formatVersion": 999 }""",
            Encoding.UTF8);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => ConfigurationService.LoadAsync(path));

        Assert.Contains("plus récent", exception.Message);
    }

    [Fact]
    public async Task CancelledSaveDoesNotReplaceExistingProfile()
    {
        var path = GetPath("cancelled.json");
        await File.WriteAllTextAsync(path, "original", Encoding.UTF8);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ConfigurationService.SaveAsync(path, new MacroConfiguration(), cancellation.Token));

        Assert.Equal("original", await File.ReadAllTextAsync(path, Encoding.UTF8));
        Assert.Empty(Directory.GetFiles(_temporaryDirectory, "*.tmp"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, true);
        }
    }

    private string GetPath(string fileName) => Path.Combine(_temporaryDirectory, fileName);
}
