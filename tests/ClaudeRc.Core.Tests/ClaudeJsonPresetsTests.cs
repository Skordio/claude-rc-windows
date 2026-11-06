using System.Text;
using ClaudeRc.Core.Claude;

namespace ClaudeRc.Core.Tests;

public sealed class ClaudeJsonPresetsTests : IDisposable
{
    private const string Folder = @"C:\Users\me\src\app";
    private const string Key = "C:/Users/me/src/app";

    private readonly TempDir _tmp = new();
    private readonly string _file;
    private readonly ClaudeJsonPresets _presets;

    public ClaudeJsonPresetsTests()
    {
        _file = _tmp.Combine(".claude.json");
        _presets = new ClaudeJsonPresets(_file);
    }

    public void Dispose() => _tmp.Dispose();

    private void WriteFile(string text) => File.WriteAllBytes(_file, new UTF8Encoding(false).GetBytes(text));
    private string ReadFile() => new UTF8Encoding(false).GetString(File.ReadAllBytes(_file));

    // Builds JSON the way claude writes it: 2-space indent, LF, no trailing newline.
    private static string J(string text) => text.Replace("\r\n", "\n").Replace('\'', '"').TrimEnd('\n');

    [Theory]
    [InlineData(@"C:\Users\me\src\app", "C:/Users/me/src/app")]
    [InlineData(@"c:\users\me\RUNS\nongitc", "c:/users/me/RUNS/nongitc")]
    [InlineData(@"\\Home\its\ITS Work\Clients", "//Home/its/ITS Work/Clients")]
    [InlineData(@"C:\", "C:/")]
    public void Project_key_is_the_path_with_forward_slashes_and_no_case_change(string path, string key)
    {
        Assert.Equal(key, ClaudeJsonPresets.ProjectKey(path));
    }

    [Fact]
    public void Adds_both_keys_to_an_existing_project_and_touches_nothing_else()
    {
        WriteFile(J("""
            {
              'numStartups': 12,
              'projects': {
                'C:/Users/me/other': {
                  'allowedTools': []
                },
                'C:/Users/me/src/app': {
                  'allowedTools': [],
                  'lastCost': 1.50
                }
              },
              'userID': 'abc'
            }
            """));

        var result = _presets.Ensure(Folder);

        Assert.Equal(PresetOutcome.Written, result.Outcome);
        Assert.Equal(J("""
            {
              'numStartups': 12,
              'projects': {
                'C:/Users/me/other': {
                  'allowedTools': []
                },
                'C:/Users/me/src/app': {
                  'allowedTools': [],
                  'lastCost': 1.50,
                  'hasTrustDialogAccepted': true,
                  'remoteControlSpawnMode': 'same-dir'
                }
              },
              'userID': 'abc'
            }
            """), ReadFile());
    }

    [Fact]
    public void Adds_a_project_entry_when_the_folder_has_none()
    {
        WriteFile(J("""
            {
              'projects': {}
            }
            """));

        Assert.Equal(PresetOutcome.Written, _presets.Ensure(Folder).Outcome);
        Assert.Equal(J("""
            {
              'projects': {
                'C:/Users/me/src/app': {
                  'hasTrustDialogAccepted': true,
                  'remoteControlSpawnMode': 'same-dir'
                }
              }
            }
            """), ReadFile());
    }

    [Fact]
    public void Adds_projects_when_absent()
    {
        WriteFile(J("""
            {
              'userID': 'abc'
            }
            """));

        Assert.Equal(PresetOutcome.Written, _presets.Ensure(Folder).Outcome);
        Assert.Equal(J("""
            {
              'userID': 'abc',
              'projects': {
                'C:/Users/me/src/app': {
                  'hasTrustDialogAccepted': true,
                  'remoteControlSpawnMode': 'same-dir'
                }
              }
            }
            """), ReadFile());
    }

    [Fact]
    public void Creates_the_file_when_absent()
    {
        Assert.Equal(PresetOutcome.Written, _presets.Ensure(Folder).Outcome);
        Assert.Equal(J("""
            {
              'projects': {
                'C:/Users/me/src/app': {
                  'hasTrustDialogAccepted': true,
                  'remoteControlSpawnMode': 'same-dir'
                }
              }
            }
            """), ReadFile());
    }

    [Fact]
    public void Keeps_a_saved_worktree_choice_and_sets_trust_in_place()
    {
        WriteFile(J("""
            {
              'projects': {
                'C:/Users/me/src/app': {
                  'hasTrustDialogAccepted': false,
                  'remoteControlSpawnMode': 'worktree',
                  'x': 1
                }
              }
            }
            """));

        Assert.Equal(PresetOutcome.Written, _presets.Ensure(Folder).Outcome);
        Assert.Equal(J("""
            {
              'projects': {
                'C:/Users/me/src/app': {
                  'hasTrustDialogAccepted': true,
                  'remoteControlSpawnMode': 'worktree',
                  'x': 1
                }
              }
            }
            """), ReadFile());
    }

    [Fact]
    public void Does_not_write_when_nothing_changes()
    {
        var text = J("""
            {
              'projects': {
                'C:/Users/me/src/app': {
                  'remoteControlSpawnMode': 'worktree',
                  'hasTrustDialogAccepted': true
                }
              }
            }
            """) + "\n";
        WriteFile(text);
        var stamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(_file, stamp);

        Assert.Equal(PresetOutcome.Unchanged, _presets.Ensure(Folder).Outcome);
        Assert.Equal(text, ReadFile());
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(_file));
        Assert.Single(Directory.GetFiles(_tmp.Path));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{\"projects\": []}")]
    [InlineData("{\"projects\": {\"C:/Users/me/src/app\": 5}}")]
    public void Refuses_a_file_it_cannot_safely_edit(string text)
    {
        WriteFile(text);

        var result = _presets.Ensure(Folder);

        Assert.Equal(PresetOutcome.Error, result.Outcome);
        Assert.False(string.IsNullOrEmpty(result.Error));
        Assert.Equal(text, ReadFile());
        Assert.Single(Directory.GetFiles(_tmp.Path));
    }

    [Fact]
    public void Key_lookup_is_case_sensitive()
    {
        WriteFile(J("""
            {
              'projects': {
                'c:/users/me/src/app': {
                  'hasTrustDialogAccepted': true,
                  'remoteControlSpawnMode': 'same-dir'
                }
              }
            }
            """));

        Assert.Equal(PresetOutcome.Written, _presets.Ensure(Folder).Outcome);
        Assert.Equal(J("""
            {
              'projects': {
                'c:/users/me/src/app': {
                  'hasTrustDialogAccepted': true,
                  'remoteControlSpawnMode': 'same-dir'
                },
                'C:/Users/me/src/app': {
                  'hasTrustDialogAccepted': true,
                  'remoteControlSpawnMode': 'same-dir'
                }
              }
            }
            """), ReadFile());
    }

    [Fact]
    public void Keeps_claude_formatting_style_for_untouched_values()
    {
        // Non-ASCII, an emoji, U+2028, control escapes, number spelling and key order all
        // come back exactly as JSON.stringify writes them.
        var odd = "x\u00e9\u2028" + char.ConvertFromUtf32(0x1F600) + "<>&'\\u0001\\u001f\\n\\t\\\"\\\\/";
        WriteFile("{\n  \"z\": \"" + odd + "\",\n  \"a\": 1e5,\n  \"m\": -0.0,\n  \"e\": {},\n  \"l\": [],\n  \"projects\": {}\n}");

        Assert.Equal(PresetOutcome.Written, _presets.Ensure(Folder).Outcome);

        var bytes = File.ReadAllBytes(_file);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "no BOM");
        Assert.Equal(
            "{\n  \"z\": \"" + odd + "\",\n  \"a\": 1e5,\n  \"m\": -0.0,\n  \"e\": {},\n  \"l\": [],\n  \"projects\": {\n" +
            "    \"C:/Users/me/src/app\": {\n      \"hasTrustDialogAccepted\": true,\n      \"remoteControlSpawnMode\": \"same-dir\"\n    }\n  }\n}",
            ReadFile());
    }

    [Fact]
    public void Replaces_the_file_atomically_without_leaving_temp_files()
    {
        WriteFile("{}");
        Assert.Equal(PresetOutcome.Written, _presets.Ensure(Folder).Outcome);
        Assert.Equal([_file], Directory.GetFiles(_tmp.Path));
    }

    [Fact]
    public void Reports_an_error_when_the_folder_of_the_file_does_not_exist()
    {
        var presets = new ClaudeJsonPresets(_tmp.Combine("nope", ".claude.json"));
        Assert.Equal(PresetOutcome.Error, presets.Ensure(Folder).Outcome);
    }
}
