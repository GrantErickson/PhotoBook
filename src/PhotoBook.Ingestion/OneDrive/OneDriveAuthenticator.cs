using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Broker;

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
/// The MSAL implementation (doc 05 "Authentication", ADR-0011): a public client application against
/// the consumers authority, delegated <c>User.Read</c> + <c>Files.Read</c> only, a DPAPI-encrypted
/// token cache under <c>%LOCALAPPDATA%</c>, silent refresh on every sync, and an interactive prompt
/// only when the refresh token is dead.
/// <para>
/// Interactive sign-in goes through the <b>Windows WAM broker</b>: the native Windows account
/// picker rather than a browser window. That gives single sign-on from the account the user is
/// already signed into Windows with, keeps refresh tokens in the OS rather than in a file we
/// manage, and supports Windows Hello and passkeys the way the platform intends. MSAL falls back
/// to the system browser by itself when the broker is unavailable, which is why the loopback
/// redirect URI stays registered (see SETUP.md).
/// </para>
/// <para>
/// WAM parents its window to the caller's, so a host with a window must supply the handle —
/// otherwise the account picker can appear behind the app or, on some Windows builds, not at all.
/// </para>
/// </summary>
public sealed class MsalOneDriveAuthenticator : IOneDriveAuthenticator
{
    private readonly OneDriveConfiguration _configuration;
    private readonly Lazy<IPublicClientApplication> _application;
    private readonly DpapiTokenCacheStorage? _cache;
    private readonly Func<IntPtr>? _parentWindow;

    /// <summary>Creates an authenticator over a configuration.</summary>
    /// <param name="configuration">
    /// The loaded configuration; it must be configured — call
    /// <see cref="OneDriveConfigurationLoader.LoadRequired"/> or
    /// <see cref="OneDriveConfiguration.EnsureConfigured"/> first.
    /// </param>
    /// <param name="parentWindow">
    /// Returns the HWND the WAM account picker should be parented to. A UI host should pass its main
    /// window handle; headless callers may omit it, in which case MSAL parents to the console or
    /// desktop window.
    /// </param>
    /// <exception cref="OneDriveNotConfiguredException">No client id has been configured (see SETUP.md).</exception>
    public MsalOneDriveAuthenticator(OneDriveConfiguration configuration, Func<IntPtr>? parentWindow = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        configuration.EnsureConfigured();
        _configuration = configuration;
        _parentWindow = parentWindow;

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

        // WAM first (ADR-0011). MSAL falls back to the system browser on its own when the broker
        // cannot run, so the loopback redirect below still matters.
        if (OperatingSystem.IsWindows())
        {
            builder = builder.WithBroker(new BrokerOptions(BrokerOptions.OperatingSystems.Windows)
            {
                Title = "PhotoBook",
            });

            if (_parentWindow is not null)
            {
                builder = builder.WithParentActivityOrWindow(_parentWindow);
            }
        }

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

        // WAM reports a missing broker redirect URI as a plain invalid-request, which is otherwise
        // an impossible error to act on.
        MsalServiceException { ErrorCode: "invalid_request" } when ex.Message.Contains("redirect", StringComparison.OrdinalIgnoreCase) =>
            "The app registration is missing the broker redirect URI. Add " +
            "ms-appx-web://microsoft.aad.brokerplugin/<your-client-id> under \"Mobile and desktop " +
            $"applications\", as described in {OneDriveNotConfiguredException.SetupDocument}.",
        _ => $"OneDrive sign-in failed: {ex.Message}",
    };
}
