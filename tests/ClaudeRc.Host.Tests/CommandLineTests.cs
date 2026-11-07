using System.Runtime.InteropServices;
using ClaudeRc.Core.Processes;

namespace ClaudeRc.Host.Tests;

public sealed partial class CommandLineTests
{
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("with space", "\"with space\"")]
    [InlineData("", "\"\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData(@"C:\dir with space\", "\"C:\\dir with space\\\\\"")]
    [InlineData(@"C:\no\space\", @"C:\no\space\")]
    [InlineData("a\\\"b", "\"a\\\\\\\"b\"")]
    [InlineData("%PATH% & 50%", "\"%PATH% & 50%\"")]
    public void Quotes_one_argument(string arg, string expected)
    {
        Assert.Equal("prog " + expected, CommandLine.Build("prog", [arg]));
    }

    [Fact]
    public void Arguments_alone_are_joined_with_the_same_quoting()
    {
        Assert.Equal("", CommandLine.JoinArguments([]));
        Assert.Equal("--instance t1 --data-dir \"C:\\a b\\\\\" \"\"",
            CommandLine.JoinArguments(["--instance", "t1", "--data-dir", @"C:\a b\", ""]));
        string[] args = ["--name", "m0 \"q\" & %PATH%", @"trailing\"];
        Assert.Equal([@"C:\x\prog.exe", .. args], Parse(@"C:\x\prog.exe " + CommandLine.JoinArguments(args)));
    }

    [Fact]
    public void Program_path_with_spaces_is_quoted()
    {
        Assert.Equal("\"C:\\Program Files\\claude.exe\" rc", CommandLine.Build(@"C:\Program Files\claude.exe", ["rc"]));
    }

    public static TheoryData<string[]> RoundTripCases() =>
    [
        ["rc", "--name", "m0 \"q\" & %PATH% 50%"],
        ["rc", "--name", @"trailing\"],
        ["rc", "--name", @"C:\dir with space\"],
        ["rc", "--name", "a\\\\\"b\\"],
        ["rc", "--name", ""],
        ["rc", "--name", "tab\there", "x"],
        ["rc", "--name", "ünïcödé ✔ 漢"],
    ];

    [Theory]
    [MemberData(nameof(RoundTripCases))]
    public void Windows_parses_back_the_same_arguments(string[] args)
    {
        var line = CommandLine.Build(@"C:\x\prog.exe", args);
        var parsed = Parse(line);
        Assert.Equal([@"C:\x\prog.exe", .. args], parsed);
    }

    private static unsafe string[] Parse(string commandLine)
    {
        var argv = CommandLineToArgvW(commandLine, out var argc);
        Assert.NotEqual(0, argv);
        try
        {
            var result = new string[argc];
            for (var i = 0; i < argc; i++)
                result[i] = Marshal.PtrToStringUni(((nint*)argv)[i])!;
            return result;
        }
        finally
        {
            LocalFree(argv);
        }
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CommandLineToArgvW(string lpCmdLine, out int pNumArgs);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint hMem);
}
