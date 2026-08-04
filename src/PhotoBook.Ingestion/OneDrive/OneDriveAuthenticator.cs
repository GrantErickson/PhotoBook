using Microsoft.Identity.Client;

namespace PhotoBook.Ingestion.OneDrive;

/// <summary>
/// Sign-in for the OneDrive connector. Sign-in is per app, not per book (ADR-0011), and it is the one
/// piece of ingestion that can require the user's attention, so it is a seam of its own: tests and
/// the Graph spike can substitute a fake without an app registration.
/// </summary>
public interface IOneDriveAuthenticator
{
    /// <summary>The signed-in account's username, or null when nobody is signed in.</summary>
    string? SignedInAccount { get; }

    /// <summary>
    /// Returns a bearer token for the configured scopes, refreshing silently when possible. Every sync
    /// starts here; an interactive prompt only happens when the refresh token is dead (doc 05).
    /// </summary>
    /// <param name="allowInteractive">
    /// False makes this a silent-only attempt — background sync on book open must never pop a window
    /// unasked; it fails and the UI offers a Sign in button.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="OneDriveNotConfiguredException">No client id has been configured (see SETUP.md).</exception>
    /// <exception cref="OneDriveSignInException">Sign-in failed or is required and was not allowed.</exception>
    Task<string> GetAccessTokenAsync(bool allowInteractive = true, CancellationToken ct = default);

    /// <summary>Signs out and clears the encrypted token cache; the project keeps working offline.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task SignOutAsync(CancellationToken ct = default);
}

/// <summary>Sign-in did not produce a token; the message is safe to show the user.</summary>
public sealed class OneDriveSignInException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="inner">The underlying MSAL failure, when there is one.</param>
    public OneDriveSignInException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

/// <summary>
/// The MSAL implementation (doc 05 "Authentication"): a public client application against the
/// consumers authority, delegated <c>User.Read</c> + <c>Files.Read</c> only, a DPAPI-encrypted token
/// cache under <c>%LOCALAPPDATA%</c>, silent refresh on every sync and an interactive system-browser
/// prompt only when the refresh token is dead.
/// <para>
/// The WAM broker is deliberately not wired up here: it needs the extra
/// <c>Microsoft.Identity.Client.Broker</c> package, and the system-browser flow works on any Windows
/// box today. Adding the broker later is a one-line <c>WithBroker</c> call on the builder below and
/// changes nothing else.
/// </para>
/// </summary>
public sealed class MsalOneDriveAuthenticator : IOneDriveAuthenticator
{
    private readonly OneDriveConfiguration _configuration;
    private readonly Lazy<IPublicClientApplication> _application;
    private readonly DpapiTokenCacheStorage? _cache;

    /// <summary>Creates an authenticator over a configuration.</summary>
    /// <param name="configuration">
    /// The loaded configuration; it must be configured — call
    /// <see cref="OneDriveConfigurationLoader.LoadRequired"/> or
    /// <see cref="OneDriveConfiguration.EnsureConfigured"/> first.
    /// </param>
    /// <exception cref="OneDriveNotConfiguredException">No client id has been configured (see SETUP.md).</exception>
    public MsalOneDriveAuthenticator(OneDriveConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        configuration.EnsureConfigured();
        _configuration = configuration;

        if (OperatingSystem.IsWindows()) _cache = new DpapiTokenCacheStorage(configuration.EffectiveTokenCachePath);
        _application = new Lazy<IPublicClientApplication>(Build, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <inheritdoc/>
    public string? SignedInAccount { get; private set; }

    /// <summary>The scopes actually requested.</summary>
    public IReadOnlyList<string> Scopes => _configuration.Scopes;

    /// <inheritdoc/>
    public async Task<string> GetAccessTokenAsync(bool allowInteractive = true, CancellationToken ct = default)
    {
        var app = _application.Value;
        var scopes = _configuration.Scopes.ToArray();

        AuthenticationResult result;
        var account = (await app.GetAccountsAsync().ConfigureAwait(false)).FirstOrDefault();
        try
        {
            result = await app.AcquireTokenSilent(scopes, account).ExecuteAsync(ct).ConfigureAwait(false);
        }
        catch (MsalUiRequiredException ex)
        {
            if (!allowInteractive)
            {
                throw new OneDriveSignInException(
                    "PhotoBook needs you to sign in to OneDrive again before it can sync.", ex);
            }

            try
            {
                var interactive = app.AcquireTokenInteractive(scopes).WithPrompt(Prompt.SelectAccount);
                if (account is not null) interactive = interactive.WithAccount(account);
                result = await interactive.ExecuteAsync(ct).ConfigureAwait(false);
            }
            catch (MsalException inner)
            {
                throw new OneDriveSignInException(DescribeFailure(inner), inner);
            }
        }
        catch (MsalException ex)
        {
            throw new OneDriveSignInException(DescribeFailure(ex), ex);
        }

        SignedInAccount = result.Account?.Username;
        return result.AccessToken;
    }

    /// <inheritdoc/>
    public async Task SignOutAsync(CancellationToken ct = default)
    {
        var app = _application.Value;
        foreach (var account in await app.GetAccountsAsync().ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            await app.RemoveAsync(account).ConfigureAwait(false);
        }

        if (OperatingSystem.IsWindows()) _cache?.Clear();
        SignedInAccount = null;
    }

    private IPublicClientApplication Build()
    {
        var builder = PublicClientApplicationBuilder
            .Create(_configuration.ClientId!)
            .WithAuthority(_configuration.Authority, validateAuthority: true)
            .WithClientName("PhotoBook")
            .WithClientVersion(typeof(MsalOneDriveAuthenticator).Assembly.GetName().Version?.ToString() ?? "1.0.0");

        builder = string.IsNullOrWhiteSpace(_configuration.RedirectUri)
            ? builder.WithDefaultRedirectUri()
            : builder.WithRedirectUri(_configuration.RedirectUri);

        var app = builder.Build();
        if (OperatingSystem.IsWindows()) _cache?.Attach(app.UserTokenCache);
        return app;
    }

    private static string DescribeFailure(MsalException ex) => ex switch
    {
        MsalClientException { ErrorCode: "authentication_canceled" } => "OneDrive sign-in was cancelled.",
        MsalServiceException { ErrorCode: "invalid_client" } =>
            "OneDrive rejected the application id. Check the client id in " +
            $"{OneDriveConfigurationLoader.DefaultConfigurationFilePath} against the app registration in " +
            $"{OneDriveNotConfiguredException.SetupDocument}.",
        MsalServiceException { ErrorCode: "unauthorized_client" } =>
            "The app registration is not allowed to sign in personal Microsoft accounts. In the Entra portal set " +
            "\"Supported account types\" to personal Microsoft accounts, as described in " +
            $"{OneDriveNotConfiguredException.SetupDocument}.",
        _ => $"OneDrive sign-in failed: {ex.Message}",
    };
}
