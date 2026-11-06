using ClaudeRc.Core.Claude;

namespace ClaudeRc.Core.Tests;

public sealed class ClaudeLocatorTests
{
    private const string Profile = @"C:\Users\me";
    private const string Native = @"C:\Users\me\.local\bin\claude.exe";
    private const long RealSize = 300_000_000;

    // The current npm shim, verbatim from M0 (docs/m0-evidence/npm-claude.cmd.txt).
    private const string NpmShim = "@ECHO off\r\nGOTO start\r\n:find_dp0\r\nSET dp0=%~dp0\r\nEXIT /b\r\n:start\r\nSETLOCAL\r\nCALL :find_dp0\r\n\"%dp0%\\..\\@anthropic-ai\\claude-code\\bin\\claude.exe\"   %*\r\n";

    // The same shim as `npm install -g` writes it, next to node_modules rather than inside it.
    private const string GlobalNpmShim = "@ECHO off\r\nGOTO start\r\n:find_dp0\r\nSET dp0=%~dp0\r\nEXIT /b\r\n:start\r\nSETLOCAL\r\nCALL :find_dp0\r\n\"%dp0%\\node_modules\\@anthropic-ai\\claude-code\\bin\\claude.exe\"   %*\r\n";

    // An older cmd-shim that runs cli.js under node.
    private const string LegacyShim = "@ECHO off\r\nGOTO start\r\n:find_dp0\r\nSET dp0=%~dp0\r\nEXIT /b\r\n:start\r\nSETLOCAL\r\nCALL :find_dp0\r\n\r\nIF EXIST \"%dp0%\\node.exe\" (\r\n  SET \"_prog=%dp0%\\node.exe\"\r\n) ELSE (\r\n  SET \"_prog=node\"\r\n  SET PATHEXT=%PATHEXT:;.JS;=;%\r\n)\r\n\r\nendLocal & goto #_undefined_# 2>NUL || title %COMSPEC% & \"%_prog%\"  \"%dp0%\\node_modules\\@anthropic-ai\\claude-code\\cli.js\" %*\r\n";

    private readonly FakeFiles _fs = new();
    private ClaudeLocator Locator => new(_fs, Profile);

    [Fact]
    public void Settings_override_wins()
    {
        _fs.Add(Native, RealSize);
        _fs.Add(@"D:\tools\claude.exe", RealSize);
        var r = Locator.Locate(@"D:\tools\claude.exe", pathVariable: null);
        Assert.True(r.Found);
        Assert.Equal(@"D:\tools\claude.exe", r.Launch!.Program);
        Assert.Empty(r.Launch.PrefixArguments);
        Assert.Equal(ClaudeSource.Settings, r.Launch.Source);
    }

    [Theory]
    [InlineData(@"D:\tools\claude.bat")]
    [InlineData(@"D:\tools\claude.ps1")]
    [InlineData(@"D:\tools\claude")]
    [InlineData(@"D:\tools\claude.com")]
    public void Override_that_is_not_an_exe_or_a_cmd_shim_is_refused(string path)
    {
        // A .bat runs through cmd.exe, which would parse the folder name in --name (SPEC 6.3).
        _fs.Add(Native, RealSize);
        _fs.Add(path, "@echo off\r\nnode cli.js %*\r\n");
        var r = Locator.Locate(path, pathVariable: null);
        Assert.False(r.Found);
        Assert.Equal($"claude path from Settings must be claude.exe or claude.cmd: {path}", r.Error);
    }

    [Fact]
    public void Exe_override_is_accepted_whatever_its_name_and_case()
    {
        _fs.Add(@"D:\tools\Claude-Dev.EXE", RealSize);
        var r = Locator.Locate(@"D:\tools\Claude-Dev.EXE", pathVariable: null);
        Assert.True(r.Found, r.Error);
        Assert.Equal(@"D:\tools\Claude-Dev.EXE", r.Launch!.Program);
    }

    [Fact]
    public void Missing_override_is_an_error_not_a_silent_fallback()
    {
        _fs.Add(Native, RealSize);
        var r = Locator.Locate(@"D:\tools\claude.exe", pathVariable: null);
        Assert.False(r.Found);
        Assert.Contains(@"D:\tools\claude.exe", r.Error);
    }

    [Fact]
    public void Native_install_comes_before_path()
    {
        _fs.Add(Native, RealSize);
        _fs.Add(@"C:\bin\claude.exe", RealSize);
        var r = Locator.Locate(null, @"C:\bin");
        Assert.Equal(Native, r.Launch!.Program);
        Assert.Equal(ClaudeSource.NativeInstall, r.Launch.Source);
    }

    [Fact]
    public void Claude_exe_on_path_is_found_in_path_order()
    {
        _fs.Add(@"C:\second\claude.exe", RealSize);
        _fs.Add(@"C:\third\claude.exe", RealSize);
        var r = Locator.Locate(null, @"C:\first;""C:\second"";;C:\third");
        Assert.Equal(@"C:\second\claude.exe", r.Launch!.Program);
        Assert.Equal(ClaudeSource.Path, r.Launch.Source);
    }

    [Fact]
    public void Claude_exe_anywhere_on_path_beats_claude_cmd_earlier_on_path()
    {
        _fs.Add(@"C:\npm\claude.cmd", GlobalNpmShim);
        _fs.Add(@"C:\npm\node_modules\@anthropic-ai\claude-code\bin\claude.exe", RealSize);
        _fs.Add(@"C:\later\claude.exe", RealSize);
        var r = Locator.Locate(null, @"C:\npm;C:\later");
        Assert.Equal(@"C:\later\claude.exe", r.Launch!.Program);
    }

    [Fact]
    public void Npm_shim_resolves_to_the_native_exe_it_launches()
    {
        _fs.Add(@"C:\Users\me\AppData\Roaming\npm\claude.cmd", GlobalNpmShim);
        _fs.Add(@"C:\Users\me\AppData\Roaming\npm\node_modules\@anthropic-ai\claude-code\bin\claude.exe", RealSize);
        var r = Locator.Locate(null, @"C:\Windows;C:\Users\me\AppData\Roaming\npm\");
        Assert.True(r.Found, r.Error);
        Assert.Equal(@"C:\Users\me\AppData\Roaming\npm\node_modules\@anthropic-ai\claude-code\bin\claude.exe", r.Launch!.Program);
        Assert.Empty(r.Launch.PrefixArguments);
        Assert.Equal(ClaudeSource.NpmShim, r.Launch.Source);
        Assert.Equal(@"C:\Users\me\AppData\Roaming\npm\claude.cmd", r.Launch.FoundAt);
    }

    [Fact]
    public void Npm_shim_resolves_from_its_own_bin_folder_too()
    {
        // `npm install --prefix x` puts the shim in x\node_modules\.bin; %dp0%\.. is node_modules.
        _fs.Add(@"C:\x\node_modules\.bin\claude.cmd", NpmShim);
        _fs.Add(@"C:\x\node_modules\@anthropic-ai\claude-code\bin\claude.exe", RealSize);
        var r = Locator.Locate(null, @"C:\x\node_modules\.bin");
        Assert.Equal(@"C:\x\node_modules\@anthropic-ai\claude-code\bin\claude.exe", r.Launch!.Program);
    }

    [Fact]
    public void Npm_placeholder_stub_is_reported_as_an_incomplete_install()
    {
        _fs.Add(@"C:\npm\claude.cmd", GlobalNpmShim);
        _fs.Add(@"C:\npm\node_modules\@anthropic-ai\claude-code\bin\claude.exe", 512);
        var r = Locator.Locate(null, @"C:\npm");
        Assert.False(r.Found);
        Assert.Equal("npm install of Claude Code is incomplete (postinstall did not run)", r.Error);
    }

    [Fact]
    public void Legacy_shim_runs_cli_js_under_the_bundled_node()
    {
        _fs.Add(@"C:\npm\claude.cmd", LegacyShim);
        _fs.Add(@"C:\npm\node.exe", RealSize);
        _fs.Add(@"C:\npm\node_modules\@anthropic-ai\claude-code\cli.js", 1000);
        var r = Locator.Locate(null, @"C:\npm");
        Assert.True(r.Found, r.Error);
        Assert.Equal(@"C:\npm\node.exe", r.Launch!.Program);
        Assert.Equal([@"C:\npm\node_modules\@anthropic-ai\claude-code\cli.js"], r.Launch.PrefixArguments);
        Assert.Equal(ClaudeSource.NpmShim, r.Launch.Source);
    }

    [Fact]
    public void Legacy_shim_falls_back_to_node_on_path()
    {
        _fs.Add(@"C:\npm\claude.cmd", LegacyShim);
        _fs.Add(@"C:\nodejs\node.exe", RealSize);
        _fs.Add(@"C:\npm\node_modules\@anthropic-ai\claude-code\cli.js", 1000);
        var r = Locator.Locate(null, @"C:\npm;C:\nodejs");
        Assert.Equal(@"C:\nodejs\node.exe", r.Launch!.Program);
        Assert.Equal([@"C:\npm\node_modules\@anthropic-ai\claude-code\cli.js"], r.Launch.PrefixArguments);
    }

    [Fact]
    public void Legacy_shim_without_node_is_an_error()
    {
        _fs.Add(@"C:\npm\claude.cmd", LegacyShim);
        _fs.Add(@"C:\npm\node_modules\@anthropic-ai\claude-code\cli.js", 1000);
        var r = Locator.Locate(null, @"C:\npm");
        Assert.False(r.Found);
        Assert.Contains("node", r.Error);
    }

    [Fact]
    public void Shim_whose_target_is_missing_is_an_error()
    {
        _fs.Add(@"C:\npm\claude.cmd", GlobalNpmShim);
        var r = Locator.Locate(null, @"C:\npm");
        Assert.False(r.Found);
        Assert.Contains(@"C:\npm\node_modules\@anthropic-ai\claude-code\bin\claude.exe", r.Error);
    }

    [Fact]
    public void Unrecognised_shim_is_an_error()
    {
        _fs.Add(@"C:\npm\claude.cmd", "@echo off\r\necho hello\r\n");
        var r = Locator.Locate(null, @"C:\npm");
        Assert.False(r.Found);
        Assert.Contains(@"C:\npm\claude.cmd", r.Error);
    }

    [Fact]
    public void Override_may_be_a_shim()
    {
        _fs.Add(@"D:\npm\claude.cmd", GlobalNpmShim);
        _fs.Add(@"D:\npm\node_modules\@anthropic-ai\claude-code\bin\claude.exe", RealSize);
        var r = Locator.Locate(@"D:\npm\claude.cmd", null);
        Assert.Equal(@"D:\npm\node_modules\@anthropic-ai\claude-code\bin\claude.exe", r.Launch!.Program);
        Assert.Equal(ClaudeSource.Settings, r.Launch.Source);
    }

    [Fact]
    public void Nothing_found_gives_the_spec_message()
    {
        var r = Locator.Locate(null, @"C:\Windows;C:\bin");
        Assert.False(r.Found);
        Assert.Null(r.Launch);
        Assert.Equal(ClaudeLocator.NotFoundMessage, r.Error);
        Assert.Equal("claude not found: install Claude Code or set its path in Settings", ClaudeLocator.NotFoundMessage);
    }

    [Fact]
    public void Relative_path_entries_are_ignored()
    {
        _fs.Add(Path.GetFullPath(@"bin\claude.exe"), RealSize);
        var r = Locator.Locate(null, "bin");
        Assert.False(r.Found);
    }

    [Fact]
    public void Real_file_probe_reports_files()
    {
        using var tmp = new TempDir();
        var f = tmp.Combine("x.cmd");
        File.WriteAllText(f, "abc");
        var probe = new RealFileProbe();
        Assert.True(probe.FileExists(f));
        Assert.False(probe.FileExists(tmp.Combine("nope")));
        Assert.False(probe.FileExists(tmp.Path));
        Assert.Equal(3, probe.FileLength(f));
        Assert.Equal("abc", probe.ReadAllText(f));
    }

    private sealed class FakeFiles : IFileProbe
    {
        private readonly Dictionary<string, (long Length, string? Text)> _files = new(StringComparer.OrdinalIgnoreCase);

        public void Add(string path, long length) => _files[path] = (length, null);
        public void Add(string path, string text) => _files[path] = (text.Length, text);

        public bool FileExists(string path) => _files.ContainsKey(Path.GetFullPath(path));
        public long FileLength(string path) => _files[Path.GetFullPath(path)].Length;
        public string ReadAllText(string path) => _files[Path.GetFullPath(path)].Text ?? throw new IOException("binary");
    }
}
