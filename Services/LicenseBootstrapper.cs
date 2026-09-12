using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace IndxServer.Services
{
    /// <summary>
    /// Service that ensures a local .license file is present at startup,
    /// downloading it from the Indx portal (hardcoded URL) when a license token is configured.
    /// </summary>
    public interface ILicenseBootstrapper
    {
        /// <summary>
        /// Ensures a local .license file at the path resolved from Indx:LicenseFile. The
        /// download URL is hardcoded to the Indx portal; when Indx:LicenseToken (or the admin-UI
        /// token) is set, it is sent as a bearer token and a fresh license is re-fetched on
        /// startup and daily. With no token configured this is a no-op. Failures are logged but
        /// never thrown, and never delete an existing file. The returned
        /// <see cref="LicenseFetchResult"/> describes the outcome so callers (e.g. the admin UI)
        /// can surface why a fetch failed instead of silently swallowing it.
        /// </summary>
        Task<LicenseFetchResult> EnsureLocalLicenseAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Read-only snapshot of the auto-fetch configuration and the resolved local
        /// license file on disk. Safe to call on a source build (the fetch/write logic
        /// is not gated on obfuscation) so the UI can show whether auto-fetch is wired
        /// up and when the file was last written, without exposing the token value.
        /// </summary>
        LicenseAutoFetchStatus GetStatus();
    }

    /// <summary>
    /// Snapshot of the license auto-fetch state for diagnostics / UI. Never carries
    /// the token value itself — only whether one is configured.
    /// </summary>
    public record LicenseAutoFetchStatus(
        bool UrlConfigured,
        string? Url,
        bool TokenConfigured,
        string LocalPath,
        bool FileExists,
        long FileBytes,
        DateTime? LastWriteUtc);

    /// <summary>Outcome of an <see cref="ILicenseBootstrapper.EnsureLocalLicenseAsync"/> call.</summary>
    public enum LicenseFetchOutcome
    {
        /// <summary>No download URL is configured — nothing was attempted.</summary>
        NotConfigured,
        /// <summary>A file was already present and no token is set, so the download was skipped.</summary>
        SkippedExisting,
        /// <summary>A fresh license file was downloaded and written to disk.</summary>
        Fetched,
        /// <summary>The fetch was attempted but failed; any existing file is left untouched.</summary>
        Failed
    }

    /// <summary>
    /// Result of a license fetch attempt, carrying a human-readable message suitable
    /// for the admin UI. Never includes the token value.
    /// </summary>
    public record LicenseFetchResult(
        LicenseFetchOutcome Outcome,
        string Message,
        int? HttpStatus = null,
        long BytesWritten = 0,
        string? LocalPath = null)
    {
        /// <summary>True when the license on disk is in the expected state (freshly fetched or already present).</summary>
        public bool Succeeded => Outcome is LicenseFetchOutcome.Fetched or LicenseFetchOutcome.SkippedExisting;
    }

    /// <summary>
    /// Downloads the .license file from the Indx license portal to a local path using a
    /// configured license token. Only Indx issues licenses, so the portal URL is hardcoded
    /// (see <see cref="DefaultDownloadUrl"/>) and not configurable.
    ///
    /// Failures are logged but never thrown - the app starts in free-tier mode
    /// (100k document limit) instead of taking the whole instance down.
    /// </summary>
    internal class LicenseBootstrapper : ILicenseBootstrapper
    {
        /// <summary>
        /// The Indx license portal endpoint. Hardcoded — only Indx issues licenses, so this is
        /// not configurable; customers supply only a license token.
        /// </summary>
        public const string DefaultDownloadUrl = "https://license.indx.co/api/license/current";

        private readonly IConfiguration _configuration;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly InstanceSettingsService _settings;
        private readonly ILogger<LicenseBootstrapper> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="LicenseBootstrapper"/>.
        /// </summary>
        public LicenseBootstrapper(
            IConfiguration configuration,
            IHttpClientFactory httpClientFactory,
            InstanceSettingsService settings,
            ILogger<LicenseBootstrapper> logger)
        {
            _configuration = configuration;
            _httpClientFactory = httpClientFactory;
            _settings = settings;
            _logger = logger;
        }

        /// <summary>
        /// Resolves the effective download URL + token. The URL is hardcoded to the Indx portal
        /// (<see cref="DefaultDownloadUrl"/>) — only Indx issues licenses, so it is not
        /// configurable. The token comes from instance settings (admin UI) or the
        /// Indx:LicenseToken app setting. The URL is returned only when a token is present, so a
        /// manual-license / no-licensing install never phones home.
        /// </summary>
        private (string? Url, string? Token) ResolveSource()
        {
            var settings = _settings.Load();
            var token = !string.IsNullOrWhiteSpace(settings.LicenseToken)
                ? settings.LicenseToken
                : _configuration["Indx:LicenseToken"];

            var url = string.IsNullOrWhiteSpace(token) ? null : DefaultDownloadUrl;
            return (url, token);
        }

        /// <inheritdoc />
        public async Task<LicenseFetchResult> EnsureLocalLicenseAsync(CancellationToken cancellationToken = default)
        {
            var (url, token) = ResolveSource();
            var localPath = ResolveLocalPath();
            // The URL is only non-null when a token is configured, so a blank URL means
            // auto-fetch is simply not set up (no token) — nothing to do.
            if (string.IsNullOrWhiteSpace(url))
            {
                return new LicenseFetchResult(
                    LicenseFetchOutcome.NotConfigured,
                    "No license token is configured.",
                    LocalPath: localPath);
            }

            // A token is configured: authenticate with it and re-fetch a fresh license (the
            // portal serves rolling 90-day Pro / 365-day Free files) on startup and daily.
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);

                using var client = _httpClientFactory.CreateClient(nameof(LicenseBootstrapper));
                client.Timeout = TimeSpan.FromSeconds(30);

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

                _logger.LogInformation("Fetching license file from {Url} to {Path}", url, localPath);
                using var response = await client.SendAsync(request, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    var status = (int)response.StatusCode;
                    // The portal usually returns a short text/JSON body explaining why
                    // (e.g. an invalid or revoked token). Surface it — truncated — so the
                    // admin sees the real cause instead of a generic failure.
                    var detail = await ReadShortBodyAsync(response, cancellationToken);
                    var hint = response.StatusCode is System.Net.HttpStatusCode.Unauthorized
                                                   or System.Net.HttpStatusCode.Forbidden
                        ? " The license token was rejected — check it is correct and not revoked."
                        : "";
                    var msg = $"License server returned {status} {response.ReasonPhrase}.{hint}"
                            + (string.IsNullOrEmpty(detail) ? "" : $" Response: {detail}");
                    _logger.LogWarning("License fetch failed: {Message}", msg);
                    return new LicenseFetchResult(
                        LicenseFetchOutcome.Failed, msg, HttpStatus: status, LocalPath: localPath);
                }

                var contentType = response.Content.Headers.ContentType?.MediaType;
                var contentEncoding = string.Join(", ", response.Content.Headers.ContentEncoding);
                _logger.LogInformation(
                    "License response: Content-Type={ContentType} Content-Encoding={ContentEncoding}",
                    contentType ?? "(none)",
                    string.IsNullOrEmpty(contentEncoding) ? "(none)" : contentEncoding);

                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);

                // Don't clobber a good local file with an empty/failed body.
                if (bytes.Length == 0)
                {
                    const string msg = "License server returned an empty response; keeping any existing file.";
                    _logger.LogWarning(msg);
                    return new LicenseFetchResult(
                        LicenseFetchOutcome.Failed, msg, HttpStatus: (int)response.StatusCode, LocalPath: localPath);
                }

                // A license file is encrypted binary served as octet-stream. If the body is HTML or
                // JSON, the URL is pointing at the wrong place (e.g. a login page or /api/license/info)
                // and writing it would replace a working license with garbage. Reject it instead.
                if (contentType is "text/html" or "application/json"
                    || (contentType is not null && contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)))
                {
                    var body = await ReadShortBodyAsync(response, cancellationToken);
                    var msg = $"License server returned {contentType}, not a license file — "
                            + "check the URL points at the license download endpoint (…/api/license/current)."
                            + (string.IsNullOrEmpty(body) ? "" : $" Response: {body}");
                    _logger.LogWarning("License fetch rejected: {Message}", msg);
                    return new LicenseFetchResult(
                        LicenseFetchOutcome.Failed, msg, HttpStatus: (int)response.StatusCode, LocalPath: localPath);
                }

                await File.WriteAllBytesAsync(localPath, bytes, cancellationToken);
                _logger.LogInformation("License file fetched ({ByteCount} bytes) to {Path}", bytes.Length, localPath);
                return new LicenseFetchResult(
                    LicenseFetchOutcome.Fetched,
                    $"License fetched ({bytes.Length} bytes) to {localPath}.",
                    HttpStatus: (int)response.StatusCode,
                    BytesWritten: bytes.Length,
                    LocalPath: localPath);
            }
            catch (Exception ex)
            {
                // Keep the existing local file (if any) — never delete a working license
                // just because the portal was unreachable.
                var msg = $"Could not reach the license server at {url}: {ex.Message}";
                _logger.LogWarning(
                    ex,
                    "Failed to fetch license file from {Url}. Keeping existing file / free-tier mode.",
                    url);
                return new LicenseFetchResult(LicenseFetchOutcome.Failed, msg, LocalPath: localPath);
            }
        }

        /// <summary>
        /// Reads up to ~300 chars of a (failed) response body for diagnostics. Never throws.
        /// </summary>
        private static async Task<string> ReadShortBodyAsync(
            HttpResponseMessage response, CancellationToken cancellationToken)
        {
            try
            {
                var body = (await response.Content.ReadAsStringAsync(cancellationToken))?.Trim() ?? "";
                const int max = 300;
                return body.Length > max ? body[..max] + "…" : body;
            }
            catch
            {
                return "";
            }
        }

        /// <inheritdoc />
        public LicenseAutoFetchStatus GetStatus()
        {
            var (url, token) = ResolveSource();
            var localPath = ResolveLocalPath();

            long bytes = 0;
            DateTime? lastWrite = null;
            var exists = File.Exists(localPath);
            if (exists)
            {
                try
                {
                    var fi = new FileInfo(localPath);
                    bytes = fi.Length;
                    lastWrite = fi.LastWriteTimeUtc;
                }
                catch
                {
                    // Best effort — a stat failure shouldn't break the page.
                }
            }

            return new LicenseAutoFetchStatus(
                UrlConfigured: !string.IsNullOrWhiteSpace(url),
                Url: url,
                TokenConfigured: !string.IsNullOrWhiteSpace(token),
                LocalPath: localPath,
                FileExists: exists,
                FileBytes: bytes,
                LastWriteUtc: lastWrite);
        }

        private string ResolveLocalPath()
        {
            var configured = _configuration["Indx:LicenseFile"];
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return configured;
            }

            // Write into ./IndxData alongside the SQLite databases, jwt.key, settings.json
            // and any uploaded *.license files. This is the SAME directory that
            // IndxServerInternalApi.GetLicensePath() scans, so a fetched license is actually
            // picked up by the search engines. (On Azure App Service the working directory is
            // under the persistent /home mount, so ./IndxData survives restarts.) Earlier this
            // returned /home/data on Azure, which the engine never reads — leaving fetched
            // licenses invisible and the instance reporting "unlicensed".
            return Path.Combine("./IndxData", "indx.license");
        }
    }
}
