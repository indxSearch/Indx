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
        /// Downloads the license file from Indx:LicenseDownloadUrl to the local
        /// path resolved from Indx:LicenseFile when the local file is absent.
        /// Failures are logged but never thrown.
        /// </summary>
        Task EnsureLocalLicenseAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Downloads the .license file from Indx:LicenseDownloadUrl to a local path
    /// when it isn't already present on disk. Used by Marketplace deployments
    /// where the customer hands over a SAS URL via createUiDefinition.
    ///
    /// Failures are logged but never thrown - the app starts in free-tier mode
    /// (100k document limit) instead of taking the whole instance down.
    /// </summary>
    public class LicenseBootstrapper : ILicenseBootstrapper
    {
        private readonly IConfiguration _configuration;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<LicenseBootstrapper> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="LicenseBootstrapper"/>.
        /// </summary>
        public LicenseBootstrapper(
            IConfiguration configuration,
            IHttpClientFactory httpClientFactory,
            ILogger<LicenseBootstrapper> logger)
        {
            _configuration = configuration;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        /// <inheritdoc />
        public async Task EnsureLocalLicenseAsync(CancellationToken cancellationToken = default)
        {
            var url = _configuration["Indx:LicenseDownloadUrl"];
            if (string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            var localPath = ResolveLocalPath();
            if (File.Exists(localPath))
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

                _logger.LogInformation("Downloading license file to {Path}", localPath);
                var bytes = await client.GetByteArrayAsync(url, cancellationToken);
                await File.WriteAllBytesAsync(localPath, bytes, cancellationToken);
                _logger.LogInformation(
                    "License file downloaded ({ByteCount} bytes)",
                    bytes.Length);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to download license file from {Url}. Continuing in free-tier mode.",
                    url);
            }
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
