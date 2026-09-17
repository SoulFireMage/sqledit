namespace SqlShell.Core.Profiles;

/// <summary>Process-local credential store used on non-Windows hosts and in tests.</summary>
public sealed class InMemoryCredentialStore : ICredentialStore
{
    private readonly Dictionary<(string Service, string Username), string> _values = [];

    public string? GetPassword(string service, string username)
        => _values.TryGetValue((service, username), out var value) ? value : null;

    public void SetPassword(string service, string username, string password)
        => _values[(service, username)] = password;

    public void DeletePassword(string service, string username)
    {
        if (!_values.Remove((service, username)))
        {
            throw new CredentialNotFoundException(service);
        }
    }
}

/// <summary>Selects the platform credential backend used by default.</summary>
public static class CredentialStore
{
    public static ICredentialStore Default()
        => OperatingSystem.IsWindows() ? new WindowsCredentialStore() : new InMemoryCredentialStore();
}
