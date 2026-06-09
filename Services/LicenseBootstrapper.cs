using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace IndxCloudApi.Services
{
    /// <summary>
    /// Service that ensures a local .license file is present at startup,
    /// downloading it from a configured SAS URL when missing.
    /// </summary>
    public interface ILicenseBootstrapper
    {
        /// <summary>
        /// Ensures a local .license file from Indx:LicenseDownloadUrl at the path
        /// resolved from Indx:LicenseFile. With Indx:LicenseToken set, sends it as a
        /// bearer token and re-fetches a fresh license on every startup; without a
        /// token, downloads only when the local file is missing. Failures are logged
        /// but never thrown, and never delete an existing file.
        /// </summary>
        Task EnsureLocalLicenseAsync(CancellationToken cancellationToken = default);

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

    /// <summary>
    /// Downloads the .license file from Indx:LicenseDownloadUrl to a local path
    /// when it isn't already present on disk. Used by Marketplace deployments
    /// where the customer hands over a SAS URL via createUiDefinition.
    ///
    /// Failures are logged but never thrown - the app starts in free-tier mode
    /// (100k document limit) instead of taking the whole instance down.
    /// </summary>
    internal class LicenseBootstrapper : ILicenseBootstrapper
    {
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
        /// Resolves the effective download URL + token: instance settings (set via the admin
        /// UI) take precedence over the Indx:LicenseDownloadUrl / Indx:LicenseToken app settings.
        /// </summary>
        private (string? Url, string? Token) ResolveSource()
        {
            var settings = _settings.Load();
            var url = !string.IsNullOrWhiteSpace(settings.LicenseDownloadUrl)
                ? settings.LicenseDownloadUrl
                : _configuration["Indx:LicenseDownloadUrl"];
            var token = !string.IsNullOrWhiteSpace(settings.LicenseToken)
                ? settings.LicenseToken
                : _configuration["Indx:LicenseToken"];
            return (url, token);
        }

        /// <inheritdoc />
        public async Task EnsureLocalLicenseAsync(CancellationToken cancellationToken = default)
        {
            var (url, token) = ResolveSource();
            if (string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            // When a portal token is configured we authenticate with it and re-fetch a
            // fresh license on EVERY startup (rolling 90-day Pro / 365-day Free files).
            // Without a token we keep the legacy behaviour: download only when missing
            // (e.g. a one-off SAS URL).
            var hasToken = !string.IsNullOrWhiteSpace(token);

            var localPath = ResolveLocalPath();
            if (!hasToken && File.Exists(localPath))
            {
                _logger.LogInformation(
                    "License file already present at {Path}; skipping download",
                    localPath);
                return;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);

                using var client = _httpClientFactory.CreateClient(nameof(LicenseBootstrapper));
                client.Timeout = TimeSpan.FromSeconds(30);

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (hasToken)
                {
                    request.Headers.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                }

                _logger.LogInformation("Fetching license file to {Path}", localPath);
                using var response = await client.SendAsync(request, cancellationToken);
                response.EnsureSuccessStatusCode();
                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);

                // Don't clobber a good local file with an empty/failed body.
                if (bytes.Length == 0)
                {
                    _logger.LogWarning("License endpoint returned no content; keeping any existing file.");
                    return;
                }

                await File.WriteAllBytesAsync(localPath, bytes, cancellationToken);
                _logger.LogInformation("License file fetched ({ByteCount} bytes)", bytes.Length);
            }
            catch (Exception ex)
            {
                // Keep the existing local file (if any) — never delete a working license
                // just because the portal was unreachable.
                _logger.LogWarning(
                    ex,
                    "Failed to fetch license file from {Url}. Keeping existing file / free-tier mode.",
                    url);
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

            // Default to ./IndxData/indx.license alongside the SQLite databases.
            // ConnectionStringHelper handles the same path translation logic.
            var azure = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME"));
            var dataDir = azure ? "/home/data" : "./IndxData";
            return Path.Combine(dataDir, "indx.license");
        }
    }
}
