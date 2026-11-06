// M0 spike: minimal ConPTY launcher. Throwaway code, deleted at the end of M1.
//
// Usage:
//   ConPtySpike.exe --exe <path> --cwd <dir> --out <prefix> [--timeout <s>]
//                   [--cols 120] [--rows 40] [--auto-stop <s>] [--stop-grace <s>]
//                   -- <arg1> <arg2> ...
//
// Writes:
//   <prefix>.raw          every output byte from the pseudo-console, unmodified
//   <prefix>.events.txt   timestamped events (start, chunks, input, pids, windows, exit)
//   <prefix>.pids.txt     job process ids (rewritten when they change)
// Control (polled every 100 ms), directory <prefix>.ctl\ :
//   *.in    raw bytes written to the pseudo-console input, then the file is deleted
//   kill    terminate the Job Object
//   close   ClosePseudoConsole
using System.Diagnostics;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Console;
using Windows.Win32.System.JobObjects;
using Windows.Win32.System.Threading;

namespace ConPtySpike;

internal static unsafe class Program
{
    private const uint PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;

    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static StreamWriter? _events;
    private static readonly object EventsLock = new();

    private static void Event(string text)
    {
        lock (EventsLock)
        {
            _events?.WriteLine($"{Clock.Elapsed.TotalMilliseconds,10:F0} ms  {DateTime.Now:HH:mm:ss.fff}  {text}");
            _events?.Flush();
        }
    }

    private static int Main(string[] argv)
    {
        string? exe = null, cwd = null, outPrefix = null;
        double timeout = 120, autoStop = -1, stopGrace = 5;
        short cols = 120, rows = 40;
        var childArgs = new List<string>();
        for (int i = 0; i < argv.Length; i++)
        {
            switch (argv[i])
            {
                case "--exe": exe = argv[++i]; break;
                case "--cwd": cwd = argv[++i]; break;
                case "--out": outPrefix = argv[++i]; break;
                case "--timeout": timeout = double.Parse(argv[++i]); break;
                case "--auto-stop": autoStop = double.Parse(argv[++i]); break;
                case "--stop-grace": stopGrace = double.Parse(argv[++i]); break;
                case "--cols": cols = short.Parse(argv[++i]); break;
                case "--rows": rows = short.Parse(argv[++i]); break;
                case "--": childArgs.AddRange(argv[(i + 1)..]); i = argv.Length; break;
                default: return 2;
            }
        }
        if (exe is null || cwd is null || outPrefix is null) return 2;

        var ctlDir = outPrefix + ".ctl";
        Directory.CreateDirectory(ctlDir);
        _events = new StreamWriter(outPrefix + ".events.txt", append: false, Encoding.UTF8);
        using var raw = new FileStream(outPrefix + ".raw", FileMode.Create, FileAccess.Write, FileShare.Read);

        var cmdLine = BuildCommandLine(exe, childArgs);
        Event($"exe={exe}");
        Event($"cwd={cwd}");
        Event($"cmdline={cmdLine}");
        for (int i = 0; i < childArgs.Count; i++) Event($"argv[{i + 1}]=<{childArgs[i]}>");

        // Pipes: we write to inWrite, the console reads inRead; the console writes outWrite, we read outRead.
        HANDLE inRead, inWrite, outRead, outWrite;
        if (!PInvoke.CreatePipe(&inRead, &inWrite, null, 0)) throw new Win32Exception("CreatePipe in");
        if (!PInvoke.CreatePipe(&outRead, &outWrite, null, 0)) throw new Win32Exception("CreatePipe out");

        HPCON hpc;
        var hr = PInvoke.CreatePseudoConsole(new COORD { X = cols, Y = rows }, inRead, outWrite, 0, &hpc);
        if (hr.Failed) throw new Win32Exception($"CreatePseudoConsole hr=0x{hr.Value:X8}");
        PInvoke.CloseHandle(inRead);
        PInvoke.CloseHandle(outWrite);
        Event($"pseudoconsole {cols}x{rows} created");

        // Job Object with KILL_ON_JOB_CLOSE.
        var jobSafe = PInvoke.CreateJobObject(null, null);
        if (jobSafe.IsInvalid) throw new Win32Exception("CreateJobObject");
        var job = (HANDLE)jobSafe.DangerousGetHandle();
        var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if (!PInvoke.SetInformationJobObject(job, JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation, &limits,
                (uint)sizeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION)))
            throw new Win32Exception("SetInformationJobObject");

        // Attribute list carrying the pseudo-console.
        nuint size = 0;
        PInvoke.InitializeProcThreadAttributeList(default, 1, 0, &size);
        var attrMem = (void*)System.Runtime.InteropServices.Marshal.AllocHGlobal((nint)size);
        var attrList = new LPPROC_THREAD_ATTRIBUTE_LIST(attrMem);
        if (!PInvoke.InitializeProcThreadAttributeList(attrList, 1, 0, &size)) throw new Win32Exception("InitAttr");
        if (!PInvoke.UpdateProcThreadAttribute(attrList, 0, PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, (void*)hpc.Value,
                (nuint)sizeof(HPCON), null, null))
            throw new Win32Exception("UpdateAttr");

        var si = new STARTUPINFOEXW();
        si.StartupInfo.cb = (uint)sizeof(STARTUPINFOEXW);
        si.lpAttributeList = attrList;
        // Without this, a parent whose own std handles are redirected (tests, Start-Process -Redirect...)
        // leaks them to the child, which then writes to them instead of the pseudo-console.
        si.StartupInfo.dwFlags = STARTUPINFOW_FLAGS.STARTF_USESTDHANDLES; // hStd* stay null
        var pi = new PROCESS_INFORMATION();
        var cmdBuf = (cmdLine + "\0").ToCharArray();
        fixed (char* pCmd = cmdBuf)
        fixed (char* pCwd = cwd)
        {
            if (!PInvoke.CreateProcess(null, new PWSTR(pCmd), null, null, false,
                    PROCESS_CREATION_FLAGS.EXTENDED_STARTUPINFO_PRESENT | PROCESS_CREATION_FLAGS.CREATE_SUSPENDED |
                    PROCESS_CREATION_FLAGS.CREATE_UNICODE_ENVIRONMENT,
                    null, new PCWSTR(pCwd), (STARTUPINFOW*)&si, &pi))
                throw new Win32Exception("CreateProcess");
        }
        if (!PInvoke.AssignProcessToJobObject(job, pi.hProcess)) throw new Win32Exception("AssignProcessToJobObject");
        PInvoke.ResumeThread(pi.hThread);
        PInvoke.CloseHandle(pi.hThread);
        Event($"started pid={pi.dwProcessId}");

        var inStream = new FileStream(new SafeFileHandle((nint)inWrite.Value, true), FileAccess.Write, 1);
        var outStream = new FileStream(new SafeFileHandle((nint)outRead.Value, true), FileAccess.Read, 1);

        long total = 0;
        var reader = new Thread(() =>
        {
            var buf = new byte[16384];
            try
            {
                int n;
                while ((n = outStream.Read(buf, 0, buf.Length)) > 0)
                {
                    lock (raw) { raw.Write(buf, 0, n); raw.Flush(); }
                    Event($"out offset={total} len={n}");
                    total += n;
                }
            }
            catch (Exception ex) { Event($"reader ended: {ex.GetType().Name} {ex.Message}"); }
            Event("reader EOF");
        }) { IsBackground = true };
        reader.Start();

        void Send(byte[] bytes, string why)
        {
            try
            {
                inStream.Write(bytes); inStream.Flush();
                Event($"input ({why}) hex={Convert.ToHexString(bytes)}");
            }
            catch (Exception ex) { Event($"input failed: {ex.Message}"); }
        }

        bool closed = false, killed = false;
        double? autoStopSentAt = null;
        string lastPids = "";
        double nextScan = 0;
        while (true)
        {
            var w = PInvoke.WaitForSingleObject(pi.hProcess, 100);
            var now = Clock.Elapsed.TotalSeconds;
            if (w == WAIT_EVENT.WAIT_OBJECT_0) break;

            foreach (var f in Directory.GetFiles(ctlDir).OrderBy(x => x))
            {
                var name = Path.GetFileName(f);
                if (name.EndsWith(".in", StringComparison.OrdinalIgnoreCase))
                {
                    byte[] bytes;
                    try { bytes = File.ReadAllBytes(f); } catch { continue; }
                    File.Delete(f);
                    Send(bytes, name);
                }
                else if (name == "kill")
                {
                    File.Delete(f);
                    Event("control: kill job");
                    PInvoke.TerminateJobObject(job, 0xDEAD);
                    killed = true;
                }
                else if (name == "close" && !closed)
                {
                    File.Delete(f);
                    Event("control: ClosePseudoConsole");
                    PInvoke.ClosePseudoConsole(hpc);
                    closed = true;
                }
            }

            if (autoStop >= 0 && autoStopSentAt is null && now >= autoStop)
            {
                Send([0x03], "auto-stop Ctrl+C");
                autoStopSentAt = now;
            }
            if (autoStopSentAt is { } sent && !killed && now >= sent + stopGrace)
            {
                Event($"auto-stop: not exited {stopGrace}s after Ctrl+C, terminating job");
                PInvoke.TerminateJobObject(job, 0xDEAD);
                killed = true;
            }
            if (!killed && now >= timeout)
            {
                Event("timeout: terminating job");
                PInvoke.TerminateJobObject(job, 0xDEAD);
                killed = true;
            }
            if (now >= nextScan)
            {
                nextScan = now + 2;
                var pids = JobPids(job);
                var s = string.Join(",", pids);
                if (s != lastPids)
                {
                    lastPids = s;
                    File.WriteAllText(outPrefix + ".pids.txt", s);
                    Event($"job pids={string.Join(",", pids.Select(Describe))}");
                }
                foreach (var win in VisibleWindows(pids)) Event($"VISIBLE WINDOW pid={win.pid} title=<{win.title}>");
            }
        }

        uint code;
        PInvoke.GetExitCodeProcess(pi.hProcess, &code);
        Event($"exited code={code} (0x{code:X8}) killed={killed} t={Clock.Elapsed.TotalSeconds:F3}s" +
              (autoStopSentAt is { } a ? $" after-ctrl-c={Clock.Elapsed.TotalSeconds - a:F3}s" : ""));
        Thread.Sleep(500);
        var left = JobPids(job);
        Event($"job pids after exit={string.Join(",", left)}");
        if (!closed) PInvoke.ClosePseudoConsole(hpc);
        reader.Join(3000);
        Event($"total bytes={total}");
        jobSafe.Dispose(); // KILL_ON_JOB_CLOSE kills anything left
        PInvoke.CloseHandle(pi.hProcess);
        _events.Dispose();
        return 0;
    }

    private static string Describe(uint pid)
    {
        try { using var p = Process.GetProcessById((int)pid); return $"{pid}:{p.ProcessName}"; }
        catch { return $"{pid}:?"; }
    }

    private static List<uint> JobPids(HANDLE job)
    {
        const int max = 256;
        var mem = stackalloc byte[sizeof(JOBOBJECT_BASIC_PROCESS_ID_LIST) + max * sizeof(nuint)];
        var list = (JOBOBJECT_BASIC_PROCESS_ID_LIST*)mem;
        list->NumberOfAssignedProcesses = max;
        var result = new List<uint>();
        if (!PInvoke.QueryInformationJobObject(job, JOBOBJECTINFOCLASS.JobObjectBasicProcessIdList, list,
                (uint)(sizeof(JOBOBJECT_BASIC_PROCESS_ID_LIST) + max * sizeof(nuint)), null))
            return result;
        var ids = (nuint*)&list->ProcessIdList;
        for (int i = 0; i < list->NumberOfProcessIdsInList; i++) result.Add((uint)ids[i]);
        return result;
    }

    [ThreadStatic] private static HashSet<uint>? _wantPids;
    [ThreadStatic] private static List<(uint pid, string title)>? _found;

    private static List<(uint pid, string title)> VisibleWindows(List<uint> pids)
    {
        _wantPids = [.. pids];
        _found = [];
        PInvoke.EnumWindows(&EnumProc, default);
        return _found;
    }

    [System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static BOOL EnumProc(HWND hwnd, LPARAM _)
    {
        uint pid;
        PInvoke.GetWindowThreadProcessId(hwnd, &pid);
        if (_wantPids!.Contains(pid) && PInvoke.IsWindowVisible(hwnd))
        {
            var buf = stackalloc char[256];
            var n = PInvoke.GetWindowText(hwnd, buf, 256);
            _found!.Add((pid, new string(buf, 0, n)));
        }
        return true;
    }

    // Windows command-line quoting (the rules CommandLineToArgvW and the MSVC CRT parse),
    // same algorithm as .NET's PasteArguments.
    internal static string BuildCommandLine(string exe, IEnumerable<string> args)
    {
        var sb = new StringBuilder();
        Append(sb, exe);
        foreach (var a in args) Append(sb, a);
        return sb.ToString();
    }

    private static void Append(StringBuilder sb, string arg)
    {
        if (sb.Length > 0) sb.Append(' ');
        if (arg.Length != 0 && arg.IndexOfAny([' ', '\t', '"']) < 0) { sb.Append(arg); return; }
        sb.Append('"');
        for (int i = 0; i < arg.Length;)
        {
            char c = arg[i++];
            if (c == '\\')
            {
                int n = 1;
                while (i < arg.Length && arg[i] == '\\') { i++; n++; }
                if (i == arg.Length) sb.Append('\\', n * 2);
                else if (arg[i] == '"') { sb.Append('\\', n * 2 + 1).Append('"'); i++; }
                else sb.Append('\\', n);
            }
            else if (c == '"') sb.Append('\\').Append('"');
            else sb.Append(c);
        }
        sb.Append('"');
    }
}

internal sealed class Win32Exception(string what)
    : Exception($"{what} failed: {System.Runtime.InteropServices.Marshal.GetLastPInvokeError()}");
