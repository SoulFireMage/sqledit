using System.Globalization;
using System.Text;
using Tomlyn;
using Tomlyn.Model;

namespace SqlShell.Core.Profiles;

/// <summary>Optional metadata changes supplied to <see cref="ProfileStore.Edit"/>.</summary>
public sealed record ProfileEdit
{
    public string? Password { get; init; }
    public string? Server { get; init; }
    public string? Database { get; init; }
    public string? Username { get; init; }
    public string? Auth { get; init; }
    public string? Driver { get; init; }
    public int? LoginTimeout { get; init; }
    public int? QueryTimeout { get; init; }
    public bool? Encrypt { get; init; }
    public bool? TrustServerCertificate { get; init; }
}

/// <summary>Connection profile persistence and credential handling.</summary>
public sealed class ProfileStore : IProfilePasswordSource
{
    public const string ServiceName = "sqlshell";

    private readonly ICredentialStore _credentials;

    public ProfileStore(string? configDir = null, ICredentialStore? credentials = null)
    {
        ConfigDir = configDir ?? DefaultConfigDir();
        Path = System.IO.Path.Combine(ConfigDir, "profiles.toml");
        _credentials = credentials ?? CredentialStore.Default();
    }

    public string ConfigDir { get; }

    /// <summary>Absolute path to <c>profiles.toml</c>.</summary>
    public string Path { get; }

    public static string DefaultConfigDir()
    {
        if (OperatingSystem.IsWindows() && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("APPDATA")))
        {
            return System.IO.Path.Combine(Environment.GetEnvironmentVariable("APPDATA")!, "sqlshell");
        }

        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (!string.IsNullOrEmpty(xdg))
        {
            return System.IO.Path.Combine(xdg, "sqlshell");
        }

        return System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "sqlshell");
    }

    public IReadOnlyList<Profile> List()
        => Read().Profiles.Select(pair => FromValues(pair.Key, pair.Value)).ToList();

    public Profile Get(string? name = null)
    {
        var data = Read();
        var selected = name ?? data.Active;
        if (string.IsNullOrEmpty(selected))
        {
            throw new ProfileException("No profile selected; use -p NAME or 'sqlshell profile use NAME'");
        }

        if (!data.Profiles.TryGetValue(selected, out var values))
        {
            throw new ProfileException($"Profile not found: {selected}");
        }

        return FromValues(selected, values);
    }

    public void Save(Profile profile, string? password = null)
    {
        profile.Validate();
        var data = Read();
        data.Profiles[profile.Name] = ToValues(profile);
        Write(data);
        if (profile.Auth == "sql" && password is not null)
        {
            _credentials.SetPassword(ServiceName, profile.Name, password);
        }
    }

    public void Use(string name)
    {
        var data = Read();
        if (!data.Profiles.ContainsKey(name))
        {
            throw new ProfileException($"Profile not found: {name}");
        }

        data.Active = name;
        Write(data);
    }

    public void Remove(string name)
    {
        var data = Read();
        if (!data.Profiles.TryGetValue(name, out var values))
        {
            throw new ProfileException($"Profile not found: {name}");
        }

        var wasSql = values.TryGetValue("auth", out var auth) && auth as string == "sql";
        data.Profiles.Remove(name);
        if (data.Active == name)
        {
            data.Active = null;
        }

        Write(data);
        if (wasSql)
        {
            try
            {
                _credentials.DeletePassword(ServiceName, name);
            }
            catch (CredentialNotFoundException)
            {
                // Already absent; removal still succeeds.
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // The credential store is unavailable in this session; the
                // profile metadata has still been removed.
            }
        }
    }

    /// <summary>Update TLS options without reading or rewriting stored credentials.</summary>
    public Profile ConfigureSecurity(
        string name,
        bool? trustServerCertificate = null,
        bool? encrypt = null)
    {
        var profile = Get(name);
        if (trustServerCertificate is null && encrypt is null)
        {
            throw new ProfileException("No security setting was supplied");
        }

        var updated = profile with
        {
            TrustServerCertificate = trustServerCertificate ?? profile.TrustServerCertificate,
            Encrypt = encrypt ?? profile.Encrypt,
        };
        Save(updated);
        return updated;
    }

    /// <summary>Edit profile metadata and optionally replace its stored password.</summary>
    public Profile Edit(string name, ProfileEdit edit)
    {
        var profile = Get(name);
        if (edit.Password is not null)
        {
            if (profile.Auth != "sql")
            {
                throw new ProfileException("Only SQL-authentication profiles have a stored password");
            }

            if (edit.Password.Length == 0)
            {
                throw new ProfileException("Password cannot be empty");
            }
        }

        var updated = profile with
        {
            Server = edit.Server ?? profile.Server,
            Database = edit.Database ?? profile.Database,
            Username = edit.Username ?? profile.Username,
            Auth = edit.Auth ?? profile.Auth,
            Driver = edit.Driver ?? profile.Driver,
            LoginTimeout = edit.LoginTimeout ?? profile.LoginTimeout,
            QueryTimeout = edit.QueryTimeout ?? profile.QueryTimeout,
            Encrypt = edit.Encrypt ?? profile.Encrypt,
            TrustServerCertificate = edit.TrustServerCertificate ?? profile.TrustServerCertificate,
        };
        updated.Validate();
        Save(updated, edit.Password);
        return updated;
    }

    public string? Password(Profile profile)
        => profile.Auth == "sql" ? _credentials.GetPassword(ServiceName, profile.Name) : null;

    private static Profile FromValues(string name, IReadOnlyDictionary<string, object?> values)
    {
        try
        {
            var profile = new Profile(
                Name: name,
                Server: GetString(values, "server") ?? string.Empty,
                Database: GetString(values, "database") ?? "master",
                Auth: GetString(values, "auth") ?? "windows",
                Username: GetString(values, "username"),
                Driver: GetString(values, "driver") ?? "ODBC Driver 18 for SQL Server",
                Encrypt: GetBool(values, "encrypt", true),
                TrustServerCertificate: GetBool(values, "trust_server_certificate", false),
                LoginTimeout: GetInt(values, "login_timeout", 8),
                QueryTimeout: GetInt(values, "query_timeout", 0));
            profile.Validate();
            return profile;
        }
        catch (ArgumentException exception)
        {
            throw new ProfileException($"Invalid profile {name}: {exception.Message}");
        }
    }

    private static Dictionary<string, object?> ToValues(Profile profile) => new()
    {
        ["server"] = profile.Server,
        ["database"] = profile.Database,
        ["auth"] = profile.Auth,
        ["username"] = profile.Username,
        ["driver"] = profile.Driver,
        ["encrypt"] = profile.Encrypt,
        ["trust_server_certificate"] = profile.TrustServerCertificate,
        ["login_timeout"] = profile.LoginTimeout,
        ["query_timeout"] = profile.QueryTimeout,
    };

    private StoreData Read()
    {
        var data = new StoreData();
        if (!File.Exists(Path))
        {
            return data;
        }

        TomlTable? model;
        try
        {
            model = TomlSerializer.Deserialize<TomlTable>(File.ReadAllText(Path));
        }
        catch (Exception exception) when (exception is not ProfileException)
        {
            throw new ProfileException($"Cannot read {Path}: {exception.Message}");
        }

        if (model is null)
        {
            return data;
        }

        var flat = model.ToDictionary(entry => entry.Key, entry => (object?)entry.Value);
        data.Active = GetString(flat, "active");
        if (model.TryGetValue("profiles", out var profiles) && profiles is TomlTable table)
        {
            foreach (var pair in table)
            {
                if (pair.Value is TomlTable values)
                {
                    data.Profiles[pair.Key] = values.ToDictionary(entry => entry.Key, entry => (object?)entry.Value);
                }
            }
        }

        return data;
    }

    private void Write(StoreData data)
    {
        Directory.CreateDirectory(ConfigDir);
        var builder = new StringBuilder();
        if (!string.IsNullOrEmpty(data.Active))
        {
            builder.Append($"active = {TomlString(data.Active)}\n");
        }

        foreach (var name in data.Profiles.Keys.OrderBy(name => name, StringComparer.Ordinal))
        {
            builder.Append($"\n[profiles.{TomlString(name)}]\n");
            foreach (var (key, value) in data.Profiles[name])
            {
                if (value is null)
                {
                    continue;
                }

                var encoded = value switch
                {
                    bool boolean => boolean ? "true" : "false",
                    int integer => integer.ToString(CultureInfo.InvariantCulture),
                    long longInteger => longInteger.ToString(CultureInfo.InvariantCulture),
                    _ => TomlString(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty),
                };
                builder.Append($"{key} = {encoded}\n");
            }
        }

        var temporary = System.IO.Path.Combine(ConfigDir, $"profiles-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporary, Path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static string TomlString(string value)
        => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\"";

    private static string? GetString(IReadOnlyDictionary<string, object?> table, string key)
    {
        if (!table.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value as string ?? Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static bool GetBool(IReadOnlyDictionary<string, object?> table, string key, bool fallback)
        => table.TryGetValue(key, out var value) && value is bool boolean ? boolean : fallback;

    private static int GetInt(IReadOnlyDictionary<string, object?> table, string key, int fallback)
    {
        if (!table.TryGetValue(key, out var value) || value is null)
        {
            return fallback;
        }

        return value switch
        {
            long longValue => (int)longValue,
            int intValue => intValue,
            _ => fallback,
        };
    }

    private sealed class StoreData
    {
        public string? Active { get; set; }

        public Dictionary<string, Dictionary<string, object?>> Profiles { get; } = [];
    }
}
