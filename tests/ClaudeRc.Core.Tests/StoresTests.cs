using System.Text.Json.Nodes;
using ClaudeRc.Core.Storage;

namespace ClaudeRc.Core.Tests;

public sealed class FoldersStoreTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly string _dataDir;
    private readonly FoldersStore _store;

    public FoldersStoreTests()
    {
        _dataDir = _tmp.Combine("data");
        _store = new FoldersStore(_dataDir);
    }

    public void Dispose() => _tmp.Dispose();

    [Fact]
    public void File_lives_in_the_data_folder()
    {
        Assert.Equal(Path.Combine(_dataDir, "folders.json"), _store.FilePath);
    }

    [Fact]
    public void Missing_file_loads_as_an_empty_list()
    {
        Assert.Empty(_store.Load());
    }

    [Fact]
    public void Save_then_load_round_trips_in_order()
    {
        _store.Save([new FolderEntry(@"C:\src\b", true), new FolderEntry(@"C:\src\a", false)]);
        Assert.Equal([new FolderEntry(@"C:\src\b", true), new FolderEntry(@"C:\src\a", false)], _store.Load());
    }

    [Fact]
    public void Saved_file_has_the_spec_shape()
    {
        _store.Save([new FolderEntry(@"C:\src\app", true)]);

        var json = JsonNode.Parse(File.ReadAllText(_store.FilePath))!.AsObject();
        Assert.Equal(["v", "folders"], json.Select(p => p.Key));
        Assert.Equal(1, (int)json["v"]!);
        var entry = json["folders"]!.AsArray().Single()!.AsObject();
        Assert.Equal(["path", "on"], entry.Select(p => p.Key));
        Assert.Equal(@"C:\src\app", (string)entry["path"]!);
        Assert.True((bool)entry["on"]!);
    }

    [Fact]
    public void Save_creates_the_data_folder_and_leaves_no_temp_file()
    {
        _store.Save([]);
        Assert.Equal([_store.FilePath], Directory.GetFiles(_dataDir));
    }

    [Fact]
    public void Save_replaces_the_previous_list()
    {
        _store.Save([new FolderEntry(@"C:\a", true)]);
        _store.Save([new FolderEntry(@"C:\b", false)]);
        Assert.Equal([new FolderEntry(@"C:\b", false)], _store.Load());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"v\":2,\"folders\":[]}")]
    [InlineData("{\"folders\":[]}")]
    [InlineData("{\"v\":1,\"folders\":[{\"on\":true}]}")]
    [InlineData("{\"v\":1}")]
    [InlineData("[]")]
    public void Unreadable_file_throws_instead_of_losing_the_list(string text)
    {
        Directory.CreateDirectory(_dataDir);
        File.WriteAllText(_store.FilePath, text);
        Assert.Throws<DataFileException>(() => _store.Load());
    }

    [Fact]
    public void Unknown_properties_are_ignored()
    {
        Directory.CreateDirectory(_dataDir);
        File.WriteAllText(_store.FilePath, "{\"v\":1,\"x\":5,\"folders\":[{\"path\":\"C:\\\\a\",\"on\":true,\"y\":1}]}");
        Assert.Equal([new FolderEntry(@"C:\a", true)], _store.Load());
    }
}

public sealed class SettingsStoreTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly SettingsStore _store;

    public SettingsStoreTests() => _store = new SettingsStore(_tmp.Combine("data"));

    public void Dispose() => _tmp.Dispose();

    [Fact]
    public void Missing_file_loads_defaults()
    {
        var s = _store.Load();
        Assert.Null(s.ClaudePath);
        Assert.True(s.Notifications);
    }

    [Fact]
    public void Defaults_are_written_with_an_explicit_null()
    {
        _store.Save(new AppSettings());
        var json = JsonNode.Parse(File.ReadAllText(_store.FilePath))!.AsObject();
        Assert.Equal(["v", "claude_path", "notifications"], json.Select(p => p.Key));
        Assert.Equal(1, (int)json["v"]!);
        Assert.Null(json["claude_path"]);
        Assert.True(json.ContainsKey("claude_path"));
        Assert.True((bool)json["notifications"]!);
    }

    [Fact]
    public void Save_then_load_round_trips()
    {
        _store.Save(new AppSettings(@"D:\tools\claude.exe", false));
        Assert.Equal(new AppSettings(@"D:\tools\claude.exe", false), _store.Load());
        Assert.Equal(Path.Combine(_tmp.Combine("data"), "settings.json"), _store.FilePath);
    }

    [Fact]
    public void Missing_notifications_key_means_on()
    {
        Directory.CreateDirectory(_tmp.Combine("data"));
        File.WriteAllText(_store.FilePath, "{\"v\":1,\"claude_path\":null}");
        Assert.True(_store.Load().Notifications);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{\"v\":7}")]
    public void Unreadable_file_throws(string text)
    {
        Directory.CreateDirectory(_tmp.Combine("data"));
        File.WriteAllText(_store.FilePath, text);
        Assert.Throws<DataFileException>(() => _store.Load());
    }
}
