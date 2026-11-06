using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeRc.Core.Storage;

/// <summary>One saved folder: a normalised path and the user's desired ON/OFF state.</summary>
/// <param name="Path">Normalised folder path (SPEC 3.2).</param>
/// <param name="On">Desired state; ON means the host keeps claude rc running there.</param>
public sealed record FolderEntry(string Path, bool On);

/// <summary>User settings (SPEC 3.4, 11).</summary>
/// <param name="ClaudePath">Override for the claude executable; <see langword="null"/> means auto-detect (SPEC 6.1).</param>
/// <param name="Notifications">Whether the tray app shows toasts (SPEC 3.5).</param>
public sealed record AppSettings(string? ClaudePath = null, bool Notifications = true);

/// <summary>
/// A data file exists but cannot be used (not JSON, wrong <c>v</c>, missing fields). It is
/// thrown instead of silently returning an empty list, so the caller never overwrites the
/// user's folder list with nothing.
/// </summary>
public sealed class DataFileException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// <c>folders.json</c> in the data folder (SPEC 11): <c>{"v":1,"folders":[{"path","on"}]}</c>.
/// Written only by the host, with an atomic replace.
/// </summary>
/// <param name="dataDir">The data folder (default <c>%APPDATA%\claude-rc-windows</c>; see <see cref="Hosting.HostOptions"/>).</param>
public sealed class FoldersStore(string dataDir)
{
    /// <summary>Full path of <c>folders.json</c>.</summary>
    public string FilePath { get; } = Path.Combine(dataDir, "folders.json");

    /// <summary>Loads the list, in saved order. A missing file is an empty list.</summary>
    /// <exception cref="DataFileException">The file exists but is not a valid v1 folder list.</exception>
    public IReadOnlyList<FolderEntry> Load()
    {
        var file = DataFile.Read(FilePath, DataFileJsonContext.Default.FoldersFile);
        if (file is null)
            return [];
        if (file.Folders is null)
            throw new DataFileException($"{FilePath}: \"folders\" is missing");
        return file.Folders
            .Select(f => f?.Path is { Length: > 0 } p
                ? new FolderEntry(p, f.On ?? false)
                : throw new DataFileException($"{FilePath}: a folder has no \"path\""))
            .ToList();
    }

    /// <summary>Replaces the saved list atomically, creating the data folder if needed.</summary>
    /// <exception cref="IOException">The file could not be written; the old file is unchanged.</exception>
    public void Save(IEnumerable<FolderEntry> folders)
    {
        var file = new FoldersFile
        {
            V = DataFile.Version,
            Folders = folders.Select(f => (FoldersFileEntry?)new FoldersFileEntry { Path = f.Path, On = f.On }).ToList(),
        };
        DataFile.Write(FilePath, file, DataFileJsonContext.Default.FoldersFile);
    }
}

/// <summary>
/// <c>settings.json</c> in the data folder (SPEC 11):
/// <c>{"v":1,"claude_path":null,"notifications":true}</c>.
/// </summary>
/// <param name="dataDir">The data folder.</param>
public sealed class SettingsStore(string dataDir)
{
    /// <summary>Full path of <c>settings.json</c>.</summary>
    public string FilePath { get; } = Path.Combine(dataDir, "settings.json");

    /// <summary>Loads the settings; a missing file or missing keys give the defaults.</summary>
    /// <exception cref="DataFileException">The file exists but is not valid v1 settings.</exception>
    public AppSettings Load()
    {
        var file = DataFile.Read(FilePath, DataFileJsonContext.Default.SettingsFile);
        if (file is null)
            return new AppSettings();
        return new AppSettings(
            string.IsNullOrWhiteSpace(file.ClaudePath) ? null : file.ClaudePath,
            file.Notifications ?? true);
    }

    /// <summary>Saves the settings atomically, creating the data folder if needed.</summary>
    /// <exception cref="IOException">The file could not be written; the old file is unchanged.</exception>
    public void Save(AppSettings settings)
    {
        DataFile.Write(FilePath, new SettingsFile
        {
            V = DataFile.Version,
            ClaudePath = settings.ClaudePath,
            Notifications = settings.Notifications,
        }, DataFileJsonContext.Default.SettingsFile);
    }
}

internal static class DataFile
{
    public const int Version = 1;

    public static T? Read<T>(string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) where T : class, IVersioned
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }

        T? value;
        try
        {
            value = JsonSerializer.Deserialize(text, type);
        }
        catch (JsonException e)
        {
            throw new DataFileException($"{path} is not valid JSON: {e.Message}", e);
        }
        if (value is null)
            throw new DataFileException($"{path} is empty (null)");
        if (value.V != Version)
            throw new DataFileException($"{path} has version {value.V?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "(none)"}, expected {Version}");
        return value;
    }

    public static void Write<T>(string path, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) =>
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(value, type), createDirectory: true);
}

internal interface IVersioned
{
    int? V { get; }
}

internal sealed class FoldersFile : IVersioned
{
    public int? V { get; set; }
    public List<FoldersFileEntry?>? Folders { get; set; }
}

internal sealed class FoldersFileEntry
{
    public string? Path { get; set; }
    public bool? On { get; set; }
}

internal sealed class SettingsFile : IVersioned
{
    public int? V { get; set; }
    public string? ClaudePath { get; set; }
    public bool? Notifications { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    WriteIndented = true,
    IndentSize = 2,
    NewLine = "\n",
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(FoldersFile))]
[JsonSerializable(typeof(SettingsFile))]
internal sealed partial class DataFileJsonContext : JsonSerializerContext;
