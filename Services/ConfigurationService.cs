using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using MacroFenetre.Models;

namespace MacroFenetre.Services;

internal static class ConfigurationService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    internal static string AutoSavePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MacroFenetre",
        "autosave.macrofenetre.json");

    internal static async Task SaveAsync(
        string path,
        MacroConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        configuration.FormatVersion = MacroConfiguration.CurrentFormatVersion;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    configuration,
                    JsonOptions,
                    cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    internal static void Save(string path, MacroConfiguration configuration)
    {
        configuration.FormatVersion = MacroConfiguration.CurrentFormatVersion;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(configuration, JsonOptions));
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    internal static async Task<MacroConfiguration> LoadAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        var configuration = await JsonSerializer.DeserializeAsync<MacroConfiguration>(stream, JsonOptions);
        return UpgradeAndValidate(
            configuration ?? throw new InvalidDataException("Le fichier de macros est vide ou invalide."));
    }

    private static MacroConfiguration UpgradeAndValidate(MacroConfiguration configuration)
    {
        if (configuration.FormatVersion > MacroConfiguration.CurrentFormatVersion)
        {
            throw new InvalidDataException(
                $"Ce profil utilise le format {configuration.FormatVersion}, plus récent que celui pris en charge par cette version de MacroFenêtre.");
        }

        configuration.SwitchKey ??= KeyConfiguration.From(KeyChoice.F8);
        configuration.SelectedWindows ??= [];
        configuration.Macros ??= [];
        configuration.ActionSequences ??= [];
        configuration.ActionSequenceDelayMs = configuration.ActionSequenceDelayMs <= 0
            ? 80
            : configuration.ActionSequenceDelayMs;
        configuration.StabilizationDelayMs = configuration.StabilizationDelayMs <= 0
            ? 260
            : configuration.StabilizationDelayMs;
        configuration.FormatVersion = MacroConfiguration.CurrentFormatVersion;
        return configuration;
    }
}
