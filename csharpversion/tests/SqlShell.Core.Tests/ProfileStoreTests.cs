using Microsoft.Data.SqlClient;
using SqlShell.Core.Connection;
using SqlShell.Core.Profiles;

namespace SqlShell.Core.Tests;

public class ProfileStoreTests
{
    private sealed class FakeKeyring : ICredentialStore
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

    [Fact]
    public void Profile_roundtrip_and_password_is_not_in_toml()
    {
        using var temp = new TempDirectory();
        var keys = new FakeKeyring();
        var store = new ProfileStore(temp.Path, keys);
        var profile = new Profile("work", "server.example", Auth: "sql", Username: "richard");

        store.Save(profile, "secret");

        Assert.Equal(profile, store.Get("work"));
        Assert.Equal("secret", store.Password(profile));
        Assert.DoesNotContain("secret", File.ReadAllText(store.Path));
    }

    [Fact]
    public void Active_and_remove()
    {
        using var temp = new TempDirectory();
        var store = new ProfileStore(temp.Path, new FakeKeyring());
        var profile = new Profile("work", "server");
        store.Save(profile);
        store.Use("work");
        Assert.Equal(profile, store.Get());
        store.Remove("work");
        Assert.Throws<ProfileException>(() => store.Get());
    }

    [Fact]
    public void Configure_security_preserves_password()
    {
        using var temp = new TempDirectory();
        var keys = new FakeKeyring();
        var store = new ProfileStore(temp.Path, keys);
        var profile = new Profile("work", "server", Auth: "sql", Username: "user");
        store.Save(profile, "secret");

        var updated = store.ConfigureSecurity("work", trustServerCertificate: true);

        Assert.True(updated.TrustServerCertificate);
        Assert.True(updated.Encrypt);
        Assert.Equal("secret", store.Password(updated));
    }

    [Fact]
    public void Edit_replaces_password_without_writing_it_to_profile()
    {
        using var temp = new TempDirectory();
        var keys = new FakeKeyring();
        var store = new ProfileStore(temp.Path, keys);
        var profile = new Profile("work", "server", Auth: "sql", Username: "user");
        store.Save(profile, "old-secret");

        var updated = store.Edit("work", new ProfileEdit { Password = "new-secret" });

        Assert.Equal(profile, updated);
        Assert.Equal("new-secret", store.Password(updated));
        var contents = File.ReadAllText(store.Path);
        Assert.DoesNotContain("old-secret", contents);
        Assert.DoesNotContain("new-secret", contents);
    }

    [Fact]
    public void Edit_metadata_preserves_password()
    {
        using var temp = new TempDirectory();
        var keys = new FakeKeyring();
        var store = new ProfileStore(temp.Path, keys);
        var profile = new Profile("work", "old-server", Auth: "sql", Username: "user");
        store.Save(profile, "secret");

        var updated = store.Edit("work", new ProfileEdit { Server = "new-server", Database = "reporting" });

        Assert.Equal("new-server", updated.Server);
        Assert.Equal("reporting", updated.Database);
        Assert.Equal("secret", store.Password(updated));
    }

    [Fact]
    public void Cannot_add_password_to_windows_profile()
    {
        using var temp = new TempDirectory();
        var store = new ProfileStore(temp.Path, new FakeKeyring());
        store.Save(new Profile("work", "server"));
        var exception = Assert.Throws<ProfileException>(
            () => store.Edit("work", new ProfileEdit { Password = "secret" }));
        Assert.Contains("SQL-authentication", exception.Message);
    }

    [Fact]
    public void Connection_strings_for_auth_modes()
    {
        var baseProfile = new Profile("p", "tcp:server,1433");

        var windows = new SqlConnectionStringBuilder(ConnectionStringFactory.Build(baseProfile, null));
        Assert.True(windows.IntegratedSecurity);

        var entra = baseProfile with { Auth = "entra" };
        var interactive = new SqlConnectionStringBuilder(ConnectionStringFactory.Build(entra, null));
        Assert.Equal(SqlAuthenticationMethod.ActiveDirectoryInteractive, interactive.Authentication);

        var sql = baseProfile with { Auth = "sql", Username = "u" };
        var sqlBuilder = new SqlConnectionStringBuilder(ConnectionStringFactory.Build(sql, "p}ass"));
        Assert.Equal("u", sqlBuilder.UserID);
        Assert.Equal("p}ass", sqlBuilder.Password);
    }
}
