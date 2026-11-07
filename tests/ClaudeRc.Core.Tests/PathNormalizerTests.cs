using ClaudeRc.Core.Paths;

namespace ClaudeRc.Core.Tests;

public sealed class PathNormalizerTests : IDisposable
{
    private readonly TempDir _tmp = new();
    // The temp root itself, as the normaliser sees it (long names, case on disk).
    private readonly string _root;

    public PathNormalizerTests() => _root = PathNormalizer.Normalize(_tmp.Path);

    public void Dispose() => _tmp.Dispose();

    [Fact]
    public void Existing_directory_keeps_its_path()
    {
        var dir = _tmp.CreateDir("App");
        Assert.Equal(Path.Combine(_root, "App"), PathNormalizer.Normalize(dir));
    }

    [Fact]
    public void Trailing_separator_is_removed()
    {
        _tmp.CreateDir("App");
        Assert.Equal(Path.Combine(_root, "App"), PathNormalizer.Normalize(Path.Combine(_root, "App") + "\\"));
        Assert.Equal(Path.Combine(_root, "App"), PathNormalizer.Normalize(Path.Combine(_root, "App") + "/"));
    }

    [Fact]
    public void Case_is_taken_from_disk()
    {
        _tmp.CreateDir("MixedCase", "Inner");
        var typed = Path.Combine(_root.ToLowerInvariant(), "mixedcase", "INNER");
        Assert.Equal(Path.Combine(_root, "MixedCase", "Inner"), PathNormalizer.Normalize(typed));
    }

    [Fact]
    public void Relative_segments_and_forward_slashes_are_resolved()
    {
        _tmp.CreateDir("a");
        _tmp.CreateDir("b");
        var typed = _root + "/a/../b/./";
        Assert.Equal(Path.Combine(_root, "b"), PathNormalizer.Normalize(typed));
    }

    [Fact]
    public void Junction_is_resolved_to_its_target()
    {
        var target = _tmp.CreateDir("RealTarget");
        var link = _tmp.Combine("link");
        TempDir.CreateJunction(link, target);
        Assert.Equal(Path.Combine(_root, "RealTarget"), PathNormalizer.Normalize(link));
        Assert.Equal(Path.Combine(_root, "RealTarget"), PathNormalizer.Normalize(link.ToUpperInvariant() + "\\"));
    }

    [Fact]
    public void Subfolder_of_a_junction_is_resolved()
    {
        _tmp.CreateDir("RealTarget", "Sub");
        var link = _tmp.Combine("link");
        TempDir.CreateJunction(link, _tmp.Combine("RealTarget"));
        Assert.Equal(Path.Combine(_root, "RealTarget", "Sub"), PathNormalizer.Normalize(Path.Combine(link, "sub")));
    }

    [Fact]
    public void Symbolic_link_is_resolved_when_one_can_be_created()
    {
        var target = _tmp.CreateDir("SymTarget");
        var link = _tmp.Combine("sym");
        try { Directory.CreateSymbolicLink(link, target); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return; // Needs Developer Mode or admin; junctions cover the same code path.
        }
        Assert.Equal(Path.Combine(_root, "SymTarget"), PathNormalizer.Normalize(link));
    }

    [Fact]
    public void Missing_path_is_normalised_lexically()
    {
        var typed = Path.Combine(_root, "does", "not", "Exist") + "\\";
        Assert.Equal(Path.Combine(_root, "does", "not", "Exist"), PathNormalizer.Normalize(typed));
    }

    [Fact]
    public void Missing_child_of_an_existing_folder_keeps_typed_case()
    {
        var typed = Path.Combine(_root, "NotThere");
        Assert.Equal(typed, PathNormalizer.Normalize(typed));
    }

    [Theory]
    [InlineData(@"C:\", @"C:\")]
    [InlineData(@"C:", @"C:\")]
    [InlineData(@"c:\", @"C:\")]
    [InlineData(@"C:\\", @"C:\")]
    public void Drive_root_keeps_its_separator(string typed, string expected)
    {
        Assert.Equal(expected, PathNormalizer.Normalize(typed));
    }

    [Theory]
    [InlineData(@"\\server\share\dir\", @"\\server\share\dir")]
    [InlineData(@"\\server\share\", @"\\server\share")]
    [InlineData(@"\\server\share\a\..\b", @"\\server\share\b")]
    public void Unc_paths_are_normalised_lexically_when_unreachable(string typed, string expected)
    {
        Assert.Equal(expected, PathNormalizer.Lexical(typed));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_path_is_rejected(string typed)
    {
        Assert.Throws<ArgumentException>(() => PathNormalizer.Normalize(typed));
    }

    [Fact]
    public void Result_never_has_the_long_path_prefix()
    {
        var dir = _tmp.CreateDir("Prefixed");
        Assert.Equal(Path.Combine(_root, "Prefixed"), PathNormalizer.Normalize(@"\\?\" + dir));
        Assert.DoesNotContain(@"\\?\", PathNormalizer.Normalize(dir));
    }

    [Fact]
    public void Equals_compares_case_insensitively()
    {
        Assert.True(PathNormalizer.Comparer.Equals(@"C:\Src\App", @"c:\src\app"));
        Assert.False(PathNormalizer.Comparer.Equals(@"C:\Src\App", @"C:\Src\App2"));
    }
}
