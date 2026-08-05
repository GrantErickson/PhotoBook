using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;
using Microsoft.Kiota.Abstractions.Authentication;

namespace PhotoBook.Ingestion.OneDrive;

/// <summary>
/// The Microsoft Graph implementation of <see cref="IOneDriveClient"/> (ADR-0011). It is read-only by
/// construction — the connector requests <c>Files.Read</c> and never issues a write — and every call
/// goes through the MSAL token from <see cref="IOneDriveAuthenticator"/>.
/// <para>
/// Downloads take the item's <c>@microsoft.graph.downloadUrl</c>, which is short-lived and needs no
/// auth header; the URL is re-fetched per sync and, on a stale-URL failure, re-fetched once more.
/// Throttling (429) and transient service errors (503) honor <c>Retry-After</c>.
/// </para>
/// </summary>
public sealed class OneDriveClient : IOneDriveClient, IDisposable
{
    private const string DownloadUrlKey = "@microsoft.graph.downloadUrl";
    private const int PageSize = 200;
    private const int MaxRetries = 4;

    private readonly GraphServiceClient _graph;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly SemaphoreSlim _driveIdGate = new(1, 1);
    private string? _driveId;

    /// <summary>Creates a client over an authenticator.</summary>
    /// <param name="authenticator">Supplies bearer tokens; sign-in happens lazily on the first call.</param>
    /// <param name="httpClient">HTTP client for content downloads; null creates one.</param>
    public OneDriveClient(IOneDriveAuthenticator authenticator, HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(authenticator);
        Authenticator = authenticator;

        var provider = new BaseBearerTokenAuthenticationProvider(new OneDriveAccessTokenProvider(authenticator));
        _graph = new GraphServiceClient(provider);
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _ownsHttp = httpClient is null;
    }

    /// <summary>
    /// Creates a client from the machine's configuration, throwing the actionable setup error when no
    /// client id exists yet (see <c>SETUP.md</c>).
    /// </summary>
    /// <param name="configurationFilePath">An explicit configuration file; null uses the documented search order.</param>
    /// <param name="parentWindow">
    /// Returns the HWND the WAM account picker parents to. A UI host should pass its main window
    /// handle so the picker cannot open behind the app.
    /// </param>
    /// <exception cref="OneDriveNotConfiguredException">OneDrive has not been set up on this machine.</exception>
    public static OneDriveClient CreateFromConfiguration(
        string? configurationFilePath = null, Func<IntPtr>? parentWindow = null)
    {
        var configuration = OneDriveConfigurationLoader.LoadRequired(configurationFilePath);
        return new OneDriveClient(new MsalOneDriveAuthenticator(configuration, parentWindow));
    }

    /// <summary>The authenticator in use, exposed so the UI can show the account and offer sign-out.</summary>
    public IOneDriveAuthenticator Authenticator { get; }

    /// <inheritdoc/>
    public async Task<string> GetDriveIdAsync(CancellationToken ct = default)
    {
        if (_driveId is not null) return _driveId;

        await _driveIdGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_driveId is not null) return _driveId;
            var drive = await RunAsync(() => _graph.Me.Drive.GetAsync(cancellationToken: ct), ct).ConfigureAwait(false);
            _driveId = drive?.Id ?? throw new OneDriveSyncException("OneDrive did not return a drive for this account.");
            return _driveId;
        }
        finally
        {
            _driveIdGate.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<OneDriveAlbum>> ListAlbumsAsync(CancellationToken ct = default)
    {
        var driveId = await GetDriveIdAsync(ct).ConfigureAwait(false);
        var response = await RunAsync(
            () => _graph.Drives[driveId].Bundles.GetAsync(config =>
            {
                config.QueryParameters.Filter = "bundle/album ne null";
                config.QueryParameters.Top = PageSize;
            }, ct), ct).ConfigureAwait(false);

        var albums = new List<OneDriveAlbum>();
        while (response is not null)
        {
            foreach (var item in response.Value ?? [])
            {
                if (item.Id is null) continue;
                albums.Add(new OneDriveAlbum(
                    item.Id,
                    item.Name ?? "(unnamed album)",
                    item.Bundle?.ChildCount,
                    item.LastModifiedDateTime?.UtcDateTime));
            }

            if (string.IsNullOrEmpty(response.OdataNextLink)) break;
            var next = response.OdataNextLink!;
            response = await RunAsync(
                () => _graph.Drives[driveId].Bundles.WithUrl(next).GetAsync(cancellationToken: ct), ct)
                .ConfigureAwait(false);
        }

        albums.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        return albums;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<OneDriveFolderEntry>> ListFoldersAsync(
        string? folderId = null, CancellationToken ct = default)
    {
        var folders = new List<OneDriveFolderEntry>();
        await foreach (var item in EnumerateChildrenAsync(folderId, ct).ConfigureAwait(false))
        {
            if (item.Folder is null || item.Id is null) continue;
            folders.Add(new OneDriveFolderEntry(
                item.Id, item.Name ?? "(unnamed folder)", DescribePath(item), item.Folder.ChildCount));
        }

        folders.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        return folders;
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<DriveItem> EnumerateAlbumItemsAsync(string albumId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(albumId);
        return EnumerateChildrenAsync(albumId, ct);
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<DriveItem> EnumerateFolderItemsAsync(
        string? folderId, bool recursive, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var queue = new Queue<string?>();
        queue.Enqueue(folderId);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            await foreach (var item in EnumerateChildrenAsync(current, ct).ConfigureAwait(false))
            {
                if (item.Folder is not null)
                {
                    if (recursive && item.Id is not null) queue.Enqueue(item.Id);
                    continue;
                }

                yield return item;
            }
        }
    }

    /// <inheritdoc/>
    public async Task<OneDriveDeltaResult> GetDeltaAsync(
        string? folderId, string? deltaLink, CancellationToken ct = default)
    {
        var driveId = await GetDriveIdAsync(ct).ConfigureAwait(false);
        var itemId = folderId ?? "root";
        var builder = _graph.Drives[driveId].Items[itemId].Delta;

        var response = string.IsNullOrWhiteSpace(deltaLink)
            ? await RunAsync(() => builder.GetAsDeltaGetResponseAsync(config =>
                config.QueryParameters.Top = PageSize, ct), ct).ConfigureAwait(false)
            : await RunAsync(() => builder.WithUrl(deltaLink).GetAsDeltaGetResponseAsync(cancellationToken: ct), ct)
                .ConfigureAwait(false);

        var items = new List<DriveItem>();
        var deleted = new List<string>();
        string? nextDeltaLink = null;

        while (response is not null)
        {
            foreach (var item in response.Value ?? [])
            {
                if (item.Id is null) continue;
                if (item.Deleted is not null) deleted.Add(item.Id);
                else if (item.Folder is null) items.Add(item);
            }

            nextDeltaLink = response.OdataDeltaLink ?? nextDeltaLink;
            if (string.IsNullOrEmpty(response.OdataNextLink)) break;

            var next = response.OdataNextLink!;
            response = await RunAsync(
                () => builder.WithUrl(next).GetAsDeltaGetResponseAsync(cancellationToken: ct), ct).ConfigureAwait(false);
        }

        return new OneDriveDeltaResult(items, deleted, nextDeltaLink);
    }

    /// <inheritdoc/>
    public Task<DriveItem?> GetItemAsync(string itemId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        return GetItemCoreAsync(itemId, ct);
    }

    /// <inheritdoc/>
    public async Task<Stream> OpenItemContentAsync(DriveItem item, long startOffset = 0, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);

        var url = TryGetDownloadUrl(item);
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (url is not null)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    if (startOffset > 0) request.Headers.Range = new RangeHeaderValue(startOffset, null);

                    var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                        .ConfigureAwait(false);

                    if (response.IsSuccessStatusCode)
                        return await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

                    var retryAfter = response.Headers.RetryAfter?.Delta;
                    var status = response.StatusCode;
                    response.Dispose();

                    if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound &&
                        item.Id is not null && attempt < MaxRetries)
                    {
                        // Download URLs are short-lived; refresh the item once and try again.
                        var refreshed = await GetItemCoreAsync(item.Id, ct).ConfigureAwait(false);
                        url = refreshed is null ? null : TryGetDownloadUrl(refreshed);
                        continue;
                    }

                    if (IsTransient(status) && attempt < MaxRetries)
                    {
                        await DelayAsync(retryAfter, attempt, ct).ConfigureAwait(false);
                        continue;
                    }

                    throw new OneDriveSyncException(
                        $"Downloading '{item.Name}' failed with HTTP {(int)status} {status}.");
                }

                // No download URL (rare): fall back to the authenticated /content endpoint.
                if (item.Id is null)
                    throw new OneDriveSyncException($"OneDrive item '{item.Name}' has no id to download from.");

                var driveId = await GetDriveIdAsync(ct).ConfigureAwait(false);
                var itemId = item.Id;
                var stream = await RunAsync(
                    () => _graph.Drives[driveId].Items[itemId].Content.GetAsync(cancellationToken: ct), ct)
                    .ConfigureAwait(false);
                if (stream is not null) return stream;
                throw new OneDriveSyncException($"OneDrive returned no content for '{item.Name}'.");
            }
            catch (HttpRequestException ex) when (attempt < MaxRetries)
            {
                _ = ex;
                await DelayAsync(null, attempt, ct).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _driveIdGate.Dispose();
        if (_ownsHttp) _http.Dispose();
        _graph.Dispose();
    }

    /// <summary>The item's short-lived, pre-authenticated download URL, when Graph supplied one.</summary>
    public static string? TryGetDownloadUrl(DriveItem item)
    {
        if (item.AdditionalData is null) return null;
        if (!item.AdditionalData.TryGetValue(DownloadUrlKey, out var value) || value is null) return null;
        var url = value.ToString();
        return string.IsNullOrWhiteSpace(url) ? null : url;
    }

    private async Task<DriveItem?> GetItemCoreAsync(string itemId, CancellationToken ct)
    {
        var driveId = await GetDriveIdAsync(ct).ConfigureAwait(false);
        return await RunAsync(() => _graph.Drives[driveId].Items[itemId].GetAsync(cancellationToken: ct), ct)
            .ConfigureAwait(false);
    }

    private async IAsyncEnumerable<DriveItem> EnumerateChildrenAsync(
        string? itemId, [EnumeratorCancellation] CancellationToken ct)
    {
        var driveId = await GetDriveIdAsync(ct).ConfigureAwait(false);
        var id = itemId ?? "root";
        var builder = _graph.Drives[driveId].Items[id].Children;

        var response = await RunAsync(
            () => builder.GetAsync(config => config.QueryParameters.Top = PageSize, ct), ct).ConfigureAwait(false);

        while (response is not null)
        {
            foreach (var item in response.Value ?? []) yield return item;

            if (string.IsNullOrEmpty(response.OdataNextLink)) break;
            var next = response.OdataNextLink!;
            response = await RunAsync(() => builder.WithUrl(next).GetAsync(cancellationToken: ct), ct)
                .ConfigureAwait(false);
        }
    }

    private static string? DescribePath(DriveItem item)
    {
        var parent = item.ParentReference?.Path;
        if (string.IsNullOrEmpty(parent)) return item.Name;
        var trimmed = parent.Contains(':') ? parent[(parent.IndexOf(':') + 1)..] : parent;
        return $"{trimmed}/{item.Name}".Replace("//", "/");
    }

    /// <summary>Runs a Graph call, translating throttling and service errors into retries then a clear failure.</summary>
    private static async Task<T?> RunAsync<T>(Func<Task<T?>> call, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return await call().ConfigureAwait(false);
            }
            catch (ODataError error) when (attempt < MaxRetries && IsTransient((HttpStatusCode)error.ResponseStatusCode))
            {
                await DelayAsync(null, attempt, ct).ConfigureAwait(false);
            }
            catch (ODataError error)
            {
                throw new OneDriveSyncException(
                    $"OneDrive returned {error.ResponseStatusCode}: {error.Error?.Message ?? error.Message}", error);
            }
            catch (HttpRequestException ex) when (attempt < MaxRetries)
            {
                _ = ex;
                await DelayAsync(null, attempt, ct).ConfigureAwait(false);
            }
        }
    }

    private static bool IsTransient(HttpStatusCode status) =>
        status is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable or
            HttpStatusCode.GatewayTimeout or HttpStatusCode.BadGateway or HttpStatusCode.RequestTimeout;

    private static Task DelayAsync(TimeSpan? retryAfter, int attempt, CancellationToken ct)
    {
        // Honor Retry-After when the service sends one; otherwise back off 1, 2, 4, 8 seconds.
        var delay = retryAfter ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
        if (delay > TimeSpan.FromMinutes(2)) delay = TimeSpan.FromMinutes(2);
        return Task.Delay(delay, ct);
    }

    /// <summary>Bridges MSAL tokens into the Graph SDK's Kiota authentication provider.</summary>
    private sealed class OneDriveAccessTokenProvider : IAccessTokenProvider
    {
        private readonly IOneDriveAuthenticator _authenticator;

        public OneDriveAccessTokenProvider(IOneDriveAuthenticator authenticator) => _authenticator = authenticator;

        public AllowedHostsValidator AllowedHostsValidator { get; } = new(["graph.microsoft.com"]);

        public Task<string> GetAuthorizationTokenAsync(
            Uri uri,
            Dictionary<string, object>? additionalAuthenticationContext = null,
            CancellationToken cancellationToken = default) =>
            _authenticator.GetAccessTokenAsync(allowInteractive: true, cancellationToken);
    }
}

/// <summary>A OneDrive sync failed in a way worth telling the user about, with the reason intact.</summary>
public sealed class OneDriveSyncException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">What failed.</param>
    /// <param name="inner">The underlying Graph or HTTP failure, when there is one.</param>
    public OneDriveSyncException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}
