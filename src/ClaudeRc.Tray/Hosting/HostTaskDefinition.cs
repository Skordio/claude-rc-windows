using System.Xml;
using System.Xml.Linq;
using ClaudeRc.Core.Processes;

namespace ClaudeRc.Tray.Hosting;

/// <summary>What the host's scheduled task is built from (SPEC 9).</summary>
internal sealed record HostTaskSpec
{
    /// <summary>The task's full name, e.g. <c>\claude-rc-windows\Host</c> (<see cref="TrayOptions.TaskPath"/>).</summary>
    public required string TaskPath { get; init; }

    /// <summary><see cref="StartMode.Logon"/> or <see cref="StartMode.Startup"/>.</summary>
    public required StartMode Mode { get; init; }

    /// <summary>Full path of ClaudeRcHost.exe in the installed (or portable) folder.</summary>
    public required string HostExe { get; init; }

    /// <summary>The host's arguments: none in production, <c>--instance</c> and its folders for a test instance.</summary>
    public IReadOnlyList<string> HostArguments { get; init; } = [];

    /// <summary>The user the host runs as, <c>DOMAIN\name</c>.</summary>
    public required string Account { get; init; }

    /// <summary>False only in tests: a disabled task never starts by itself.</summary>
    public bool Enabled { get; init; } = true;
}

/// <summary>The host task's XML (Task Scheduler schema 1.2), built and read without Task Scheduler.</summary>
internal static class HostTaskDefinition
{
    /// <summary>The Task Scheduler XML namespace.</summary>
    public const string Namespace = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    /// <summary><c>0x8007052E</c>, ERROR_LOGON_FAILURE: the stored password no longer works (SPEC 9.2).</summary>
    public const int LogonFailure = unchecked((int)0x8007052E);

    /// <summary>SCHED_S_TASK_HAS_NOT_RUN.</summary>
    public const int HasNotRun = 0x41303;

    /// <summary>SCHED_S_TASK_RUNNING.</summary>
    public const int TaskIsRunning = 0x41301;

    private static readonly XNamespace Ns = Namespace;

    /// <summary>
    /// The task's XML. Logon (9.1): a logon trigger for this user, "run only when user is logged
    /// on". Startup (9.2): a boot trigger, "run whether user is logged on or not" with the stored
    /// password. Both: the settings of 9.3, normal priority, least privilege.
    /// </summary>
    /// <exception cref="ArgumentException">A relative host path, no account, or no start mode.</exception>
    public static string BuildXml(HostTaskSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (!Path.IsPathFullyQualified(spec.HostExe))
            throw new ArgumentException($"the host path must be a full path: {spec.HostExe}", nameof(spec));
        if (string.IsNullOrWhiteSpace(spec.Account))
            throw new ArgumentException("the task needs the user it runs as", nameof(spec));
        var logon = LogonFor(spec.Mode);

        var trigger = spec.Mode == StartMode.Logon
            ? new XElement(Ns + "LogonTrigger", new XElement(Ns + "Enabled", "true"), new XElement(Ns + "UserId", spec.Account))
            : new XElement(Ns + "BootTrigger", new XElement(Ns + "Enabled", "true"));

        var exec = new XElement(Ns + "Exec",
            // Always quoted: unquoted, a path with spaces could be read as C:\Users\Jo.exe.
            new XElement(Ns + "Command", "\"" + spec.HostExe + "\""));
        if (spec.HostArguments.Count > 0)
            exec.Add(new XElement(Ns + "Arguments", CommandLine.JoinArguments(spec.HostArguments)));
        if (Path.GetDirectoryName(spec.HostExe) is { Length: > 0 } dir)
            exec.Add(new XElement(Ns + "WorkingDirectory", dir));

        var task = new XElement(Ns + "Task", new XAttribute("version", "1.2"),
            new XElement(Ns + "RegistrationInfo",
                new XElement(Ns + "Description",
                    "Starts the claude-rc-windows host (ClaudeRcHost.exe), which keeps claude rc running in the folders chosen in the app.")),
            new XElement(Ns + "Triggers", trigger),
            new XElement(Ns + "Principals",
                new XElement(Ns + "Principal", new XAttribute("id", PrincipalId),
                    new XElement(Ns + "UserId", spec.Account),
                    new XElement(Ns + "LogonType", logon == TaskLogon.Password ? "Password" : "InteractiveToken"),
                    new XElement(Ns + "RunLevel", "LeastPrivilege"))),
            new XElement(Ns + "Settings",
                new XElement(Ns + "MultipleInstancesPolicy", "IgnoreNew"),
                new XElement(Ns + "DisallowStartIfOnBatteries", "false"),
                new XElement(Ns + "StopIfGoingOnBatteries", "false"),
                new XElement(Ns + "AllowHardTerminate", "true"),
                new XElement(Ns + "StartWhenAvailable", "true"),
                new XElement(Ns + "RunOnlyIfNetworkAvailable", "false"),
                new XElement(Ns + "IdleSettings",
                    new XElement(Ns + "StopOnIdleEnd", "false"),
                    new XElement(Ns + "RestartOnIdle", "false")),
                new XElement(Ns + "AllowStartOnDemand", "true"),
                new XElement(Ns + "Enabled", spec.Enabled ? "true" : "false"),
                new XElement(Ns + "Hidden", "false"),
                new XElement(Ns + "RunOnlyIfIdle", "false"),
                new XElement(Ns + "WakeToRun", "false"),
                new XElement(Ns + "ExecutionTimeLimit", "PT0S"),
                // 4-6 is a normal priority class; Task Scheduler's default 7 is below normal, and
                // every claude rc the host starts would inherit it.
                new XElement(Ns + "Priority", "5"),
                new XElement(Ns + "RestartOnFailure",
                    new XElement(Ns + "Interval", "PT1M"),
                    new XElement(Ns + "Count", "999"))),
            new XElement(Ns + "Actions", new XAttribute("Context", PrincipalId), exec));
        return new XDocument(task).ToString();
    }

    private const string PrincipalId = "Author";

    /// <summary>The start mode of a task's XML: its only trigger is a logon or a boot trigger; anything else is unknown.</summary>
    public static StartMode ReadMode(string xml)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml ?? "");
        }
        catch (XmlException)
        {
            return StartMode.Unknown;
        }
        var triggers = doc.Root?.Element(Ns + "Triggers")?.Elements().ToList() ?? [];
        if (triggers.Count != 1)
            return StartMode.Unknown;
        return triggers[0].Name.LocalName switch
        {
            "LogonTrigger" => StartMode.Logon,
            "BootTrigger" => StartMode.Startup,
            _ => StartMode.Unknown,
        };
    }

    /// <summary>How the task logs on in a mode.</summary>
    /// <exception cref="ArgumentException">Not logon or startup.</exception>
    public static TaskLogon LogonFor(StartMode mode) => mode switch
    {
        StartMode.Logon => TaskLogon.InteractiveToken,
        StartMode.Startup => TaskLogon.Password,
        _ => throw new ArgumentException($"no task for start mode {mode}", nameof(mode)),
    };
}
