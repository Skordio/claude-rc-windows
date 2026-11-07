using ClaudeRc.Core.Paths;

namespace ClaudeRc.Core.Tests;

public sealed class DisplayNamesTests
{
    [Fact]
    public void Unique_basenames_are_used_as_is()
    {
        var names = DisplayNames.Compute([@"C:\src\app", @"C:\src\web", @"D:\Other"]);
        Assert.Equal("app", names[@"C:\src\app"]);
        Assert.Equal("web", names[@"C:\src\web"]);
        Assert.Equal("Other", names[@"D:\Other"]);
    }

    [Fact]
    public void Duplicate_basenames_get_their_parent()
    {
        var names = DisplayNames.Compute([@"C:\work\app", @"D:\home\app", @"C:\src\web"]);
        Assert.Equal(@"work\app", names[@"C:\work\app"]);
        Assert.Equal(@"home\app", names[@"D:\home\app"]);
        Assert.Equal("web", names[@"C:\src\web"]);
    }

    [Fact]
    public void Basenames_compare_case_insensitively()
    {
        var names = DisplayNames.Compute([@"C:\a\App", @"C:\b\app"]);
        Assert.Equal(@"a\App", names[@"C:\a\App"]);
        Assert.Equal(@"b\app", names[@"C:\b\app"]);
    }

    [Fact]
    public void Same_parent_and_name_adds_more_segments_until_unique()
    {
        var names = DisplayNames.Compute([@"C:\x\a\app", @"C:\y\a\app", @"C:\z\b\app"]);
        Assert.Equal(@"x\a\app", names[@"C:\x\a\app"]);
        Assert.Equal(@"y\a\app", names[@"C:\y\a\app"]);
        Assert.Equal(@"b\app", names[@"C:\z\b\app"]);
    }

    [Fact]
    public void Folder_directly_under_a_drive_root_uses_the_drive_as_parent()
    {
        var names = DisplayNames.Compute([@"C:\app", @"D:\x\app"]);
        Assert.Equal(@"C:\app", names[@"C:\app"]);
        Assert.Equal(@"x\app", names[@"D:\x\app"]);
    }

    [Fact]
    public void Drive_root_is_named_by_its_path()
    {
        var names = DisplayNames.Compute([@"C:\"]);
        Assert.Equal(@"C:\", names[@"C:\"]);
    }

    [Fact]
    public void Unc_folder_uses_its_basename()
    {
        var names = DisplayNames.Compute([@"\\server\share\proj"]);
        Assert.Equal("proj", names[@"\\server\share\proj"]);
    }

    [Fact]
    public void Lookup_is_case_insensitive()
    {
        var names = DisplayNames.Compute([@"C:\src\app"]);
        Assert.Equal("app", names[@"c:\SRC\app"]);
    }
}
