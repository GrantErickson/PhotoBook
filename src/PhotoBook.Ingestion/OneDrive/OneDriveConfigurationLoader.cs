using System.Text.Json;
using PhotoBook.Core.Persistence;

namespace PhotoBook.Ingestion.OneDrive;

/// <summary>
/// Finds the OneDrive client id (ADR-0011). Resolution order, first hit wins:
/// <list type="number">
/// <item><description>an explicit path passed by the caller;</description></item>
/// <item><description>the environment variable <c>PHOTOBOOK_ONEDRIVE_CLIENT_ID</c> (with optional
/// <c>PHOTOBOOK_ONEDRIVE_AUTHORITY</c> and <c>PHOTOBOOK_ONEDRIVE_REDIRECT_URI</c>) — handy for a
/// one-off run or a CI check;</description></item>
/// <item><description>the file named by <c>PHOTOBOOK_ONEDRIVE_CONFIG</c>;</description></item>
/// <item><description><c>%LOCALAPPDATA%\PhotoBook\onedrive.json</c> — the documented location.</description></item>
/// </list>
/// Nothing here reads or writes the project folder: the client id is a per-machine setting, and
/// <c>book.json</c> stays shareable (doc 05).
/// </summary>
public static class OneDriveConfigurationLoader
{
    /// <summary>Environment variable holding the Entra application (client) id.</summary>
    public const string ClientIdEnvironmentVariable = "PHOTOBOOK_ONEDRIVE_CLIENT_ID";

    /// <summary>Environment variable overriding the MSAL authority.</summary>
    public const string AuthorityEnvironmentVariable = "PHOTOBOOK_ONEDRIVE_AUTHORITY";

    /// <summary>Environment variable overriding the redirect URI.</summary>
    public const string RedirectUriEnvironmentVariable = "PHOTOBOOK_ONEDRIVE_REDIRECT_URI";

    /// <summary>Environment variable pointing at an alternative configuration file.</summary>
    public const string ConfigFileEnvironmentVariable = "PHOTOBOOK_ONEDRIVE_CONFIG";

    /// <summary>File name of the configuration document.</summary>
    public const string ConfigFileName = "onedrive.json";

    /// <summary>File name of the DPAPI-encrypted MSAL token cache.</summary>
    public const string TokenCacheFileName = "msal.cache";

    /// <summary>The per-user application data folder PhotoBook keeps machine-local settings in.</summary>
    public static string AppDataFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoBook");

    /// <summary>The documented configuration file path, <c>%LOCALAPPDATA%\PhotoBook\onedrive.json</c>.</summary>
    public static string DefaultConfigurationFilePath => Path.Combine(AppDataFolder, ConfigFileName);

    /// <summary>The documented token cache path, <c>%LOCALAPPDATA%\PhotoBook\msal.cache</c>.</summary>
    public static string DefaultTokenCachePath => Path.Combine(AppDataFolder, TokenCacheFileName);

    /// <summary>
    /// Loads the configuration, returning an unconfigured instance rather than throwing when no client
    /// id exists — callers that only want to know whether OneDrive is available check
    /// <see cref="OneDriveConfiguration.IsConfigured"/>.
    /// </summary>
    /// <param name="configurationFilePath">An explicit file to read; null uses the documented search order.</param>
    public static OneDriveConfiguration Load(string? configurationFilePath = null)
    {
        var checkedPlaces = new List<string>();

        if (!string.IsNullOrWhiteSpace(configurationFilePath))
        {
            var explicitConfig = TryReadFile(configurationFilePath, checkedPlaces);
            if (explicitConfig is not null) return explicitConfig;
        }

        var envClientId = Environment.GetEnvironmentVariable(ClientIdEnvironmentVariable);
        checkedPlaces.Add($"environment variable {ClientIdEnvironmentVariable}");
        if (OneDriveConfiguration.IsUsableClientId(envClientId))
        {
            return new OneDriveConfiguration
            {
                ClientId = envClientId!.Trim(),
                Authority = Environment.GetEnvironmentVariable(AuthorityEnvironmentVariable) is { Length: > 0 } authority
                    ? authority
                    : OneDriveConfiguration.ConsumersAuthority,
                RedirectUri = Environment.GetEnvironmentVariable(RedirectUriEnvironmentVariable),
                SourceDescription = $"environment variable {ClientIdEnvironmentVariable}",
            };
        }

        if (Environment.GetEnvironmentVariable(ConfigFileEnvironmentVariable) is { Length: > 0 } fromEnvFile)
        {
            var envFileConfig = TryReadFile(fromEnvFile, checkedPlaces);
            if (envFileConfig is not null) return envFileConfig;
        }

        var defaultConfig = TryReadFile(DefaultConfigurationFilePath, checkedPlaces);
        if (defaultConfig is not null) return defaultConfig;

        return new OneDriveConfiguration { SourceDescription = string.Join(", ", checkedPlaces) };
    }

    /// <summary>Loads the configuration and throws the actionable setup error when it is not configured.</summary>
    /// <param name="configurationFilePath">An explicit file to read; null uses the documented search order.</param>
    /// <exception cref="OneDriveNotConfiguredException">No usable client id was found.</exception>
    public static OneDriveConfiguration LoadRequired(string? configurationFilePath = null)
    {
        var configuration = Load(configurationFilePath);
        configuration.EnsureConfigured();
        return configuration;
    }

    /// <summary>
    /// Writes a placeholder <c>onedrive.json</c> the user can fill in, if none exists yet. Returns the
    /// path either way, so the UI can say "put your client id here" and open the file.
    /// </summary>
    /// <param name="configurationFilePath">Where to write; null uses <see cref="DefaultConfigurationFilePath"/>.</param>
    public static string EnsureTemplateFile(string? configurationFilePath = null)
    {
        var path = string.IsNullOrWhiteSpace(configurationFilePath) ? DefaultConfigurationFilePath : configurationFilePath;
        var folder = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
        if (!File.Exists(path)) File.WriteAllText(path, OneDriveConfiguration.SampleJson);
        return path;
    }

    private static OneDriveConfiguration? TryReadFile(string path, List<string> checkedPlaces)
    {
        checkedPlaces.Add(path);
        try
        {
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            var parsed = JsonSerializer.Deserialize<OneDriveConfiguration>(json, ProjectJson.Options);
            if (parsed is null || !parsed.IsConfigured) return null;

            return parsed with
            {
                ClientId = parsed.ClientId!.Trim(),
                Authority = string.IsNullOrWhiteSpace(parsed.Authority)
                    ? OneDriveConfiguration.ConsumersAuthority
                    : parsed.Authority,
                Scopes = parsed.Scopes is { Count: > 0 } ? parsed.Scopes : OneDriveConfiguration.DefaultScopes,
                SourceDescription = path,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A malformed or unreadable settings file must not crash the app; it reads as "not set up".
            return null;
        }
    }
}
