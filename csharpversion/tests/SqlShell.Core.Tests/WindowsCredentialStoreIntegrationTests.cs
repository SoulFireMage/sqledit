using System.ComponentModel;
using SqlShell.Core.Profiles;

namespace SqlShell.Core.Tests;

/// <summary>
/// Exercises the real Windows Credential Manager backend, including the
/// compound-target collision behaviour shared with python-keyring.
/// </summary>
/// <remarks>
/// Credential Manager requires an interactive logon session. Non-interactive
/// sessions (for example a test runner launched through WSL interop) fail with
/// ERROR_NO_SUCH_LOGON_SESSION (1312), in which case the test is inconclusive
/// and returns without asserting.
/// </remarks>
public class WindowsCredentialStoreIntegrationTests
{
    private const int NoSuchLogonSession = 1312;

    [Fact]
    public void Stores_reads_collides_and_deletes()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var store = new WindowsCredentialStore();
        var service = "sqlshell-selftest-" + Guid.NewGuid().ToString("N");
        try
        {
            store.SetPassword(service, "user1", "pä$$wörd");
            Assert.Equal("pä$$wörd", store.GetPassword(service, "user1"));

            // A second username on the same service moves the first credential to a compound target.
            store.SetPassword(service, "user2", "beta");
            Assert.Equal("pä$$wörd", store.GetPassword(service, "user1"));
            Assert.Equal("beta", store.GetPassword(service, "user2"));

            store.DeletePassword(service, "user1");
            store.DeletePassword(service, "user2");
            Assert.Null(store.GetPassword(service, "user1"));
            Assert.Null(store.GetPassword(service, "user2"));
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == NoSuchLogonSession)
        {
            return;
        }
        finally
        {
            foreach (var username in new[] { "user1", "user2" })
            {
                try
                {
                    store.DeletePassword(service, username);
                }
                catch (CredentialNotFoundException)
                {
                }
                catch (Win32Exception exception) when (exception.NativeErrorCode == NoSuchLogonSession)
                {
                }
            }
        }
    }
}
