using System.Globalization;

namespace PhotoBook.Ingestion.OneDrive;

/// <summary>
/// Everything the OneDrive connector needs to sign in (ADR-0011): the Entra <b>application (client)
/// id</b>, the authority, the delegated scopes, and where the encrypted token cache lives.
/// <para>
/// The client id is the one value PhotoBook cannot ship for the user — it comes from an app
/// registration in their own Microsoft account. Until it exists, every OneDrive entry point fails
/// with <see cref="OneDriveNotConfiguredException"/>, which names <c>SETUP.md</c> and the exact file
/// to create; nothing crashes and nothing silently does nothing. The moment a client id is supplied,
/// the connector works with no code change.
/// </para>
/// <para>Nothing here ever enters the project folder: tokens and ids are per machine and per user,
/// while <c>book.json</c> and friends must stay shareable and human-diffable (doc 05).</para>
/// </summary>
public sealed record OneDriveConfiguration
{
    /// <summary>Personal Microsoft accounts only — this is a family-photos app (doc 05 "Authentication").</summary>
    public const string ConsumersAuthority = "https://login.microsoftonline.com/consumers";

    /// <summary>The two delegated scopes, read-only by design: PhotoBook never writes to OneDrive.</summary>
    public static IReadOnlyList<string> DefaultScopes { get; } = ["User.Read", "Files.Read"];

    /// <summary>Schema version of <c>onedrive.json</c>; currently 1.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>
    /// The placeholder client id in <see cref="SampleJson"/>. It is <see cref="Guid.Empty"/>, so
    /// <see cref="IsUsableClientId"/> rejects it and copying the sample unedited reads as "not set
    /// up yet".
    /// </summary>
    public const string SampleClientId = "00000000-0000-0000-0000-000000000000";

    /// <summary>
    /// The Entra application (client) id of the user's own public-client app registration. Null or a
    /// placeholder means "not set up yet".
    /// </summary>
    public string? ClientId { get; init; }

    /// <summary>The MSAL authority; defaults to <see cref="ConsumersAuthority"/>.</summary>
    public string Authority { get; init; } = ConsumersAuthority;

    /// <summary>
    /// Redirect URI of the app registration. Leave this null: sign-in goes through the WAM broker,
    /// which uses its own <c>ms-appx-web://microsoft.aad.brokerplugin/{client-id}</c> redirect, and
    /// MSAL's desktop default (<c>http://localhost</c>) covers the browser fallback. Setting it
    /// explicitly only matters if the registration deviates from what SETUP.md describes.
    /// </summary>
    public string? RedirectUri { get; init; }

    /// <summary>The delegated scopes to request; defaults to <see cref="DefaultScopes"/>.</summary>
    public IReadOnlyList<string> Scopes { get; init; } = DefaultScopes;

    /// <summary>
    /// Where the MSAL token cache is written, DPAPI-encrypted for the current user. Null uses
    /// <c>%LOCALAPPDATA%\PhotoBook\msal.cache</c>.
    /// </summary>
    public string? TokenCachePath { get; init; }

    /// <summary>Where this configuration was read from, for error messages; not serialized.</summary>
    public string? SourceDescription { get; init; }

    /// <summary>True when <see cref="ClientId"/> is a real GUID rather than absent or a placeholder.</summary>
    public bool IsConfigured => IsUsableClientId(ClientId);

    /// <summary>The token cache path actually in effect.</summary>
    public string EffectiveTokenCachePath =>
        string.IsNullOrWhiteSpace(TokenCachePath) ? OneDriveConfigurationLoader.DefaultTokenCachePath : TokenCachePath!;

    /// <summary>Throws <see cref="OneDriveNotConfiguredException"/> unless a real client id is present.</summary>
    /// <exception cref="OneDriveNotConfiguredException">No usable client id has been configured yet.</exception>
    public void EnsureConfigured()
    {
        if (!IsConfigured) throw new OneDriveNotConfiguredException(this);
    }

    /// <summary>
    /// Every placeholder client id that has appeared in setup instructions. Copying the sample file
    /// without editing it is the single most likely setup mistake, and it must produce the friendly
    /// "not set up yet" message rather than an opaque MSAL rejection at sign-in.
    /// </summary>
    private static readonly HashSet<Guid> PlaceholderClientIds =
    [
        Guid.Empty,
        new("11111111-2222-3333-4444-555555555555"),
    ];

    /// <summary>True for a value that is a GUID and not one of the documented placeholders.</summary>
    public static bool IsUsableClientId(string? clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId)) return false;
        var trimmed = clientId.Trim().Trim('<', '>', '"', '{', '}');
        if (!Guid.TryParse(trimmed, out var guid)) return false;
        return !PlaceholderClientIds.Contains(guid);
    }

    /// <summary>The sample <c>onedrive.json</c> quoted in setup instructions and in the error message.</summary>
    public static string SampleJson => string.Create(CultureInfo.InvariantCulture, $$"""
        {
          "schemaVersion": 1,
          "clientId": "{{SampleClientId}}",
          "authority": "{{ConsumersAuthority}}",
          "scopes": [ "User.Read", "Files.Read" ]
        }
        """);
}
