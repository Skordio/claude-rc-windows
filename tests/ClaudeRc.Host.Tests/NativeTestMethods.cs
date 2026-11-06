using System.Runtime.InteropServices;

namespace ClaudeRc.Host.Tests;

internal static partial class NativeTestMethods
{
    /// <summary>With a null handler: 1 makes this process (and its future children) ignore Ctrl+C, 0 restores it.</summary>
    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetConsoleCtrlHandler(nint handlerRoutine, int add);
}
