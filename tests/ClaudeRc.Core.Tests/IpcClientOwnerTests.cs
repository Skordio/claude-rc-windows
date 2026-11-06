using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using ClaudeRc.Core.Ipc;

namespace ClaudeRc.Core.Tests;

/// <summary>
/// <see cref="IpcClient"/> trusts a pipe only when its owner is the user's own SID. It must not
/// compare against the token's default owner (what <c>PipeOptions.CurrentUserOnly</c> does): in
/// an elevated process that is BUILTIN\Administrators, so an elevated client refused the host's
/// pipe, whose owner is always the user (CI runs elevated).
/// </summary>
public sealed class IpcClientOwnerTests
{
    private static SecurityIdentifier User => WindowsIdentity.GetCurrent().User!;

    private static NamedPipeServerStream CreateUserOwnedPipe(string name)
    {
        var security = new PipeSecurity();
        security.SetOwner(User);
        security.AddAccessRule(new PipeAccessRule(User, PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 0, 0, security);
    }

    [Fact]
    public async Task Connects_to_a_pipe_owned_by_the_expected_user()
    {
        var name = "claude-rc-test-" + Guid.NewGuid().ToString("N");
        await using var server = CreateUserOwnedPipe(name);
        var accept = server.WaitForConnectionAsync();

        await using var client = await IpcClient.ConnectAsync(name, TimeSpan.FromSeconds(10), User);
        await accept;
    }

    [Fact]
    public async Task Refuses_a_pipe_whose_owner_is_not_the_expected_user()
    {
        // Locally the pipe can only be owned by the user, so ask for another owner instead: the
        // same mismatch as a pipe that another account created under our name.
        var name = "claude-rc-test-" + Guid.NewGuid().ToString("N");
        await using var server = CreateUserOwnedPipe(name);
        var accept = server.WaitForConnectionAsync();
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

        var e = await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => IpcClient.ConnectAsync(name, TimeSpan.FromSeconds(10), administrators));
        Assert.Contains(User.Value, e.Message, StringComparison.Ordinal);
        await accept; // it did connect, and then hung up
    }

    [Theory]
    [InlineData(WellKnownSidType.BuiltinAdministratorsSid)]
    [InlineData(WellKnownSidType.LocalSystemSid)]
    public void Owner_check_accepts_only_the_user(WellKnownSidType other)
    {
        IpcClient.EnsureOwnedBy(User, User);
        Assert.Throws<UnauthorizedAccessException>(
            () => IpcClient.EnsureOwnedBy(new SecurityIdentifier(other, null), User));
        Assert.Throws<UnauthorizedAccessException>(() => IpcClient.EnsureOwnedBy(null, User));
    }
}
