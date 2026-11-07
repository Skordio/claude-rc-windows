using System.Runtime.InteropServices;
using System.Security.Principal;
using ClaudeRc.Core.Adoption;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Windows.Win32.System.Diagnostics.ToolHelp;
using Windows.Win32.System.Threading;
using ComFileTime = System.Runtime.InteropServices.ComTypes.FILETIME;
using WdkPInvoke = Windows.Wdk.PInvoke;
using WdkProcessInfo = Windows.Wdk.System.Threading.PROCESSINFOCLASS;

namespace ClaudeRc.Host.Adoption;

/// <summary>A process as the Toolhelp snapshot lists it.</summary>
/// <param name="Pid">Process id.</param>
/// <param name="ParentPid">Parent id at creation.</param>
/// <param name="ImageName">Image file name.</param>
internal readonly record struct ProcessEntry(int Pid, int ParentPid, string ImageName);

/// <summary>
/// The thin Windows layer under <see cref="ExternalSessionMatcher"/> (SPEC 7): lists processes and
/// reads, for the candidates only, their creation time, token user, command line and current
/// directory (from the PEB). Never changes a process.
/// </summary>
/// <remarks>
/// <para>
/// The command line and current directory come from the target's
/// <c>RTL_USER_PROCESS_PARAMETERS</c>, found through <c>NtQueryInformationProcess</c> → PEB. A 32-bit
/// target on 64-bit Windows (WOW64) has its own 32-bit PEB, found with
/// <c>ProcessWow64Information</c> and read with the 32-bit layout. An x64 process on ARM64 is not
/// WOW64 and has the 64-bit layout.
/// </para>
/// <para>
/// A process that cannot be opened for reading (another user, an elevated or protected process)
/// simply has no details: nothing here throws for it. An elevated claude rc is therefore not seen by
/// a host that is not elevated.
/// </para>
/// </remarks>
internal static unsafe class WindowsProcesses
{
    // RTL_USER_PROCESS_PARAMETERS offsets (64-bit / 32-bit layout) and PEB.ProcessParameters.
    private const int Peb64ProcessParameters = 0x20;
    private const int Peb32ProcessParameters = 0x10;
    private const int Params64CurrentDirectory = 0x38;
    private const int Params64CommandLine = 0x70;
    private const int Params32CurrentDirectory = 0x24;
    private const int Params32CommandLine = 0x40;
    private const int MaxStringBytes = 0xFFFE;
    private const int MaxAncestors = 64;

    /// <summary>Every running process as the Toolhelp snapshot lists it.</summary>
    public static IReadOnlyList<ProcessEntry> List()
    {
        var snapshot = PInvoke.CreateToolhelp32Snapshot(CREATE_TOOLHELP_SNAPSHOT_FLAGS.TH32CS_SNAPPROCESS, 0);
        if (snapshot.IsNull || (nint)snapshot.Value == -1)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(), "CreateToolhelp32Snapshot failed");
        try
        {
            var list = new List<ProcessEntry>(512);
            var entry = new PROCESSENTRY32W { dwSize = (uint)sizeof(PROCESSENTRY32W) };
            if (!PInvoke.Process32FirstW(snapshot, &entry))
                return list;
            do
            {
                list.Add(new ProcessEntry((int)entry.th32ProcessID, (int)entry.th32ParentProcessID, entry.szExeFile.ToString()));
                entry.dwSize = (uint)sizeof(PROCESSENTRY32W);
            }
            while (PInvoke.Process32NextW(snapshot, &entry));
            return list;
        }
        finally
        {
            PInvoke.CloseHandle(snapshot);
        }
    }

    /// <summary>
    /// Every running process with its id, parent id and image name. Candidates (by image name) get
    /// their details; their ancestors get their creation time, so the matcher can follow them.
    /// </summary>
    public static IReadOnlyList<ProcessFacts> Snapshot(Func<string, bool> isCandidateImage)
    {
        ArgumentNullException.ThrowIfNull(isCandidateImage);
        var entries = List();
        var byPid = new Dictionary<int, ProcessEntry>(entries.Count);
        foreach (var e in entries)
            byPid.TryAdd(e.Pid, e);

        var facts = new Dictionary<int, ProcessFacts>(byPid.Count);
        foreach (var e in byPid.Values)
            facts[e.Pid] = new ProcessFacts { Pid = e.Pid, ParentPid = e.ParentPid, ImageName = e.ImageName };

        foreach (var candidate in byPid.Values.Where(e => e.Pid > 0 && isCandidateImage(e.ImageName)).ToList())
        {
            if (Read(candidate.Pid, candidate) is not { } read)
                continue;
            facts[candidate.Pid] = read;
            // Creation times up the parent chain, so the matcher can tell a parent from a reused id.
            var child = read;
            for (var depth = 0; depth < MaxAncestors && child.StartTime is not null; depth++)
            {
                if (!facts.TryGetValue(child.ParentPid, out var parent) || parent.Pid == child.Pid)
                    break;
                if (parent.StartTime is null)
                {
                    if (StartTimeOf(parent.Pid) is not { } start)
                        break;
                    facts[parent.Pid] = parent = parent with { StartTime = start };
                }
                child = parent;
            }
        }
        return [.. facts.Values];
    }

    /// <summary>One process's details, or null when it does not exist (any more). Fields it cannot read are null.</summary>
    public static ProcessFacts? Read(int pid)
    {
        if (pid <= 0)
            return null;
        var entry = List().FirstOrDefault(e => e.Pid == pid);
        return entry.ImageName is null ? null : Read(pid, entry);
    }

    private static ProcessFacts? Read(int pid, ProcessEntry entry)
    {
        var facts = new ProcessFacts { Pid = pid, ParentPid = entry.ParentPid, ImageName = entry.ImageName };
        var full = true;
        var h = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_INFORMATION | PROCESS_ACCESS_RIGHTS.PROCESS_VM_READ, false, (uint)pid);
        if (h.IsNull)
        {
            full = false;
            h = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        }
        if (h.IsNull)
            return facts;
        try
        {
            facts = facts with { StartTime = StartTime(h), OwnerSid = OwnerSid(h) };
            if (!full)
                return facts;
            var (cwd, commandLine) = ReadParameters(h);
            return facts with
            {
                CurrentDirectory = cwd,
                Arguments = commandLine is null ? null : SplitArguments(commandLine),
            };
        }
        finally
        {
            PInvoke.CloseHandle(h);
        }
    }

    /// <summary>The creation time of a process (FILETIME ticks), or null when it cannot be opened.</summary>
    public static long? StartTimeOf(int pid)
    {
        if (pid <= 0)
            return null;
        var h = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (h.IsNull)
            return null;
        try
        {
            return StartTime(h);
        }
        finally
        {
            PInvoke.CloseHandle(h);
        }
    }

    /// <summary>Whether the process with this id is still the one created at <paramref name="startTime"/> and has not exited.</summary>
    public static bool IsAlive(int pid, long startTime)
    {
        if (pid <= 0)
            return false;
        var h = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_ACCESS_RIGHTS.PROCESS_SYNCHRONIZE, false, (uint)pid);
        if (h.IsNull)
            return false;
        try
        {
            return StartTime(h) == startTime && PInvoke.WaitForSingleObject(h, 0) == WAIT_EVENT.WAIT_TIMEOUT;
        }
        finally
        {
            PInvoke.CloseHandle(h);
        }
    }

    /// <summary>The creation time (FILETIME ticks) of an open process.</summary>
    internal static long? StartTime(HANDLE process)
    {
        ComFileTime creation, exit, kernel, user;
        if (!PInvoke.GetProcessTimes(process, &creation, &exit, &kernel, &user))
            return null;
        return ((long)(uint)creation.dwHighDateTime << 32) | (uint)creation.dwLowDateTime;
    }

    private static string? OwnerSid(HANDLE process)
    {
        HANDLE token;
        if (!PInvoke.OpenProcessToken(process, TOKEN_ACCESS_MASK.TOKEN_QUERY, &token))
            return null;
        try
        {
            uint size = 0;
            PInvoke.GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenUser, null, 0, &size);
            if (size == 0 || size > 4096)
                return null;
            var buffer = stackalloc byte[(int)size];
            if (!PInvoke.GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenUser, buffer, size, &size))
                return null;
            var user = (TOKEN_USER*)buffer;
            return new SecurityIdentifier((nint)user->User.Sid.Value).Value;
        }
        finally
        {
            PInvoke.CloseHandle(token);
        }
    }

    /// <summary>
    /// The current directory and command line from the process's parameters block, or nulls when
    /// they cannot be read (the process exited, is still being created, or has another layout).
    /// </summary>
    internal static (string? CurrentDirectory, string? CommandLine) ReadParameters(HANDLE process)
    {
        BOOL wow64;
        if (!PInvoke.IsWow64Process(process, &wow64))
            return (null, null);

        if (wow64 && Environment.Is64BitProcess)
        {
            nuint peb32 = 0;
            uint length;
            var status = WdkPInvoke.NtQueryInformationProcess(process, WdkProcessInfo.ProcessWow64Information, &peb32, (uint)sizeof(nuint), &length);
            if (status.SeverityCode != NTSTATUS.Severity.Success || peb32 == 0)
                return (null, null);
            if (ReadUInt32(process, peb32 + Peb32ProcessParameters) is not { } parameters || parameters == 0)
                return (null, null);
            return (ReadString32(process, parameters + Params32CurrentDirectory), ReadString32(process, parameters + Params32CommandLine));
        }

        if (!wow64 && !Environment.Is64BitProcess)
            return (null, null); // a 32-bit host cannot read a 64-bit process's memory this way

        PROCESS_BASIC_INFORMATION info;
        uint returned;
        var basic = WdkPInvoke.NtQueryInformationProcess(process, WdkProcessInfo.ProcessBasicInformation, &info, (uint)sizeof(PROCESS_BASIC_INFORMATION), &returned);
        if (basic.SeverityCode != NTSTATUS.Severity.Success || info.PebBaseAddress is null)
            return (null, null);
        var peb = (nuint)info.PebBaseAddress;
        if (Environment.Is64BitProcess)
        {
            if (ReadUInt64(process, peb + Peb64ProcessParameters) is not { } parameters || parameters == 0)
                return (null, null);
            var p = (nuint)parameters;
            return (ReadString64(process, p + Params64CurrentDirectory), ReadString64(process, p + Params64CommandLine));
        }
        else
        {
            if (ReadUInt32(process, peb + Peb32ProcessParameters) is not { } parameters || parameters == 0)
                return (null, null);
            return (ReadString32(process, parameters + Params32CurrentDirectory), ReadString32(process, parameters + Params32CommandLine));
        }
    }

    private static bool ReadMemory(HANDLE process, nuint address, void* buffer, int size)
    {
        nuint read;
        return PInvoke.ReadProcessMemory(process, (void*)address, buffer, (nuint)size, &read) && read == (nuint)size;
    }

    private static uint? ReadUInt32(HANDLE process, nuint address)
    {
        uint value;
        return ReadMemory(process, address, &value, sizeof(uint)) ? value : null;
    }

    private static ulong? ReadUInt64(HANDLE process, nuint address)
    {
        ulong value;
        return ReadMemory(process, address, &value, sizeof(ulong)) ? value : null;
    }

    // UNICODE_STRING, 64-bit: USHORT Length, USHORT MaximumLength, (pad 4), PWSTR Buffer.
    private static string? ReadString64(HANDLE process, nuint address)
    {
        var header = stackalloc byte[16];
        if (!ReadMemory(process, address, header, 16))
            return null;
        var length = *(ushort*)header;
        var buffer = *(ulong*)(header + 8);
        return ReadChars(process, (nuint)buffer, length);
    }

    // UNICODE_STRING32: USHORT Length, USHORT MaximumLength, ULONG Buffer.
    private static string? ReadString32(HANDLE process, nuint address)
    {
        var header = stackalloc byte[8];
        if (!ReadMemory(process, address, header, 8))
            return null;
        var length = *(ushort*)header;
        var buffer = *(uint*)(header + 4);
        return ReadChars(process, buffer, length);
    }

    private static string? ReadChars(HANDLE process, nuint buffer, int lengthBytes)
    {
        if (lengthBytes == 0)
            return "";
        if (buffer == 0 || lengthBytes > MaxStringBytes || lengthBytes % 2 != 0)
            return null;
        var chars = new char[lengthBytes / 2];
        fixed (char* p = chars)
        {
            if (!ReadMemory(process, buffer, p, lengthBytes))
                return null;
        }
        return new string(chars);
    }

    /// <summary>The arguments after the program, split as <c>CommandLineToArgvW</c> does (the usual C runtime rules).</summary>
    internal static IReadOnlyList<string> SplitArguments(string commandLine)
    {
        // An empty command line would give this process's own path as argv[0].
        if (string.IsNullOrWhiteSpace(commandLine))
            return [];
        int count;
        PWSTR* argv;
        fixed (char* p = commandLine)
            argv = PInvoke.CommandLineToArgv(new PCWSTR(p), &count);
        if (argv is null)
            return [];
        try
        {
            var args = new string[Math.Max(0, count - 1)];
            for (var i = 1; i < count; i++)
                args[i - 1] = argv[i].ToString();
            return args;
        }
        finally
        {
            PInvoke.LocalFree((HLOCAL)(nint)argv);
        }
    }
}
