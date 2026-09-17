namespace SqlShell.Core.Profiles;

/// <summary>Secret storage abstraction, matching the Python keyring surface used by sqlshell.</summary>
public interface ICredentialStore
{
    string? GetPassword(string service, string username);

    void SetPassword(string service, string username, string password);

    void DeletePassword(string service, string username);
}

/// <summary>Raised when a credential is expected but not present in the store.</summary>
public sealed class CredentialNotFoundException(string target)
    : Exception($"No credential found for '{target}'");

/// <summary>The narrow profile-store surface the connection manager depends on.</summary>
public interface IProfilePasswordSource
{
    string? Password(Profile profile);
}
