using ClaudeRc.Host.Sessions;
using Microsoft.Win32;

namespace ClaudeRc.Host.Tests;

public sealed class SessionEnvironmentTests
{
    private static IEnumerable<string> PathEntries(string? value) =>
        (value ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string? RegistryPath(RegistryKey root, string subKey)
    {
        using var key = root.OpenSubKey(subKey);
        return key?.GetValue("Path") is string s ? Environment.ExpandEnvironmentVariables(s) : null;
    }

    [Fact]
    public void Built_from_the_registry_with_system_and_user_path_merged()
    {
        var env = SessionEnvironment.FromRegistry();

        Assert.False(string.IsNullOrEmpty(env["SystemRoot"]));
        Assert.False(string.IsNullOrEmpty(env["USERPROFILE"]));
        var path = PathEntries(env["PATH"]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var systemPath = RegistryPath(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment");
        var userPath = RegistryPath(Registry.CurrentUser, "Environment");
        Assert.NotNull(systemPath);
        Assert.All(PathEntries(systemPath), p => Assert.Contains(p, path));
        Assert.All(PathEntries(userPath), p => Assert.Contains(p, path));
    }

    [Fact]
    public void Does_not_inherit_variables_set_only_in_the_host_process()
    {
        const string Name = "CLAUDE_RC_TEST_ONLY_IN_PROCESS";
        Environment.SetEnvironmentVariable(Name, "1");
        try
        {
            Assert.False(SessionEnvironment.FromRegistry().ContainsKey(Name));
        }
        finally
        {
            Environment.SetEnvironmentVariable(Name, null);
        }
    }

    [Fact]
    public void Lookup_is_case_insensitive()
    {
        var env = SessionEnvironment.FromRegistry();
        Assert.Equal(env["PATH"], env["path"]);
    }

    [Fact]
    public void Block_is_sorted_and_double_nul_terminated()
    {
        var block = SessionEnvironment.ToBlock(new Dictionary<string, string>
        {
            ["b"] = "2",
            ["A"] = "1",
            ["c"] = "x=y",
        });
        Assert.Equal("A=1\0b=2\0c=x=y\0\0", block);
    }

    [Fact]
    public void Empty_block_is_two_nuls()
    {
        Assert.Equal("\0\0", SessionEnvironment.ToBlock(new Dictionary<string, string>()));
    }

    [Fact]
    public void Names_with_equals_or_nul_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => SessionEnvironment.ToBlock(new Dictionary<string, string> { ["a=b"] = "1" }));
        Assert.Throws<ArgumentException>(() => SessionEnvironment.ToBlock(new Dictionary<string, string> { ["a"] = "1\0" }));
    }
}
