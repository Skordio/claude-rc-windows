using System.Security.AccessControl;
using System.Security.Principal;

namespace ClaudeRc.Host;

/// <summary>
/// The single-host mutex (SPEC 9.4): <c>Global\claude-rc-windows-host-&lt;SID&gt;[-instance]</c>,
/// open only to the user and SYSTEM. Hold it for the host's whole life, on the thread that took it.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;

    private SingleInstance(Mutex mutex) => _mutex = mutex;

    /// <summary>Takes the mutex, or returns null when another host holds it (or the name is not ours).</summary>
    public static SingleInstance? TryAcquire(string name, SecurityIdentifier user)
    {
        var security = new MutexSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new MutexAccessRule(user, MutexRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            MutexRights.FullControl, AccessControlType.Allow));
        Mutex mutex;
        bool created;
        try
        {
            mutex = MutexAcl.Create(initiallyOwned: true, name, out created, security);
        }
        catch (UnauthorizedAccessException)
        {
            return null; // exists, and not accessible to us
        }
        if (!created)
        {
            bool owned;
            try
            {
                owned = mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                owned = true; // the previous host died holding it; it is ours now
            }
            if (!owned)
            {
                mutex.Dispose();
                return null;
            }
        }
        return new SingleInstance(mutex);
    }

    /// <summary>Releases the mutex.</summary>
    public void Dispose()
    {
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not owned by this thread: closing the handle is enough.
        }
        _mutex.Dispose();
    }
}
