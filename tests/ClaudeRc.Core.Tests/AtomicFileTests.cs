using ClaudeRc.Core.Storage;

namespace ClaudeRc.Core.Tests;

public sealed class AtomicFileTests : IDisposable
{
    private readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    [Fact]
    public void Creates_a_missing_file()
    {
        var path = _tmp.Combine("a.json");
        AtomicFile.WriteAllText(path, "héllo");
        Assert.Equal([0x68, 0xC3, 0xA9, 0x6C, 0x6C, 0x6F], File.ReadAllBytes(path));
        Assert.Equal([path], Directory.GetFiles(_tmp.Path));
    }

    [Fact]
    public void Replaces_an_existing_file()
    {
        var path = _tmp.Combine("a.json");
        File.WriteAllText(path, "old");
        AtomicFile.WriteAllText(path, "new");
        Assert.Equal("new", File.ReadAllText(path));
        Assert.Equal([path], Directory.GetFiles(_tmp.Path));
    }

    [Fact]
    public void Creates_the_parent_folder()
    {
        var path = _tmp.Combine("sub", "dir", "a.json");
        AtomicFile.WriteAllText(path, "x", createDirectory: true);
        Assert.Equal("x", File.ReadAllText(path));
    }

    [Fact]
    public void Leaves_no_temp_file_when_the_replace_fails()
    {
        var path = _tmp.Combine("a.json");
        File.WriteAllText(path, "old");
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() => AtomicFile.WriteAllText(path, "new"));
        }
        Assert.Equal("old", File.ReadAllText(path));
        Assert.Equal([path], Directory.GetFiles(_tmp.Path));
    }
}
