using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Serialization;
using IndxServer.Data;

namespace IndxServer.Services
{
    /// <summary>
    /// A release version as Indx writes them, compared the SemVer way. Tags on GitHub read
    /// <c>v2.0-beta.2</c>, the assembly says <c>2.0.0-beta.2</c>; both are the same version here: a
    /// leading <c>v</c> is dropped, a missing minor or patch is 0, a build suffix (<c>+sha</c>) is
    /// ignored, and a pre-release sorts before its release (2.0.0-beta.2 &lt; 2.0.0).
    /// </summary>
    public sealed record ReleaseVersion(int Major, int Minor, int Patch, IReadOnlyList<string> PreRelease) : IComparable<ReleaseVersion>
    {
        public static ReleaseVersion? Parse(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var s = text.Trim();
            if (s.StartsWith('v') || s.StartsWith('V')) s = s[1..];
            var plus = s.IndexOf('+');
            if (plus >= 0) s = s[..plus];
            var dash = s.IndexOf('-');
            var core = dash >= 0 ? s[..dash] : s;
            var pre = dash >= 0 ? s[(dash + 1)..].Split('.', StringSplitOptions.RemoveEmptyEntries) : [];
            var parts = core.Split('.');
            if (parts.Length is < 1 or > 3) return null;
            var nums = new int[3];
            for (int i = 0; i < parts.Length; i++)
                if (!int.TryParse(parts[i], out nums[i]) || nums[i] < 0) return null;
            return new ReleaseVersion(nums[0], nums[1], nums[2], pre);
        }

        public int CompareTo(ReleaseVersion? other)
        {
            if (other is null) return 1;
            int c = Major.CompareTo(other.Major);
            if (c == 0) c = Minor.CompareTo(other.Minor);
            if (c == 0) c = Patch.CompareTo(other.Patch);
            if (c != 0) return c;
            // A release outranks any of its pre-releases.
            if (PreRelease.Count == 0 || other.PreRelease.Count == 0) return other.PreRelease.Count.CompareTo(PreRelease.Count);
            for (int i = 0; i < Math.Min(PreRelease.Count, other.PreRelease.Count); i++)
            {
                var a = PreRelease[i]; var b = other.PreRelease[i];
                bool an = int.TryParse(a, out var ai), bn = int.TryParse(b, out var bi);
                c = an && bn ? ai.CompareTo(bi) : an ? -1 : bn ? 1 : string.CompareOrdinal(a, b);
                if (c != 0) return c;
            }
            return PreRelease.Count.CompareTo(other.PreRelease.Count);
        }

        public override string ToString() =>
            $"{Major}.{Minor}.{Patch}" + (PreRelease.Count > 0 ? "-" + string.Join('.', PreRelease) : "");

        /// <summary>This server's own version, from its informational version.</summary>
        public static ReleaseVersion? Current { get; } = Parse(
            typeof(ReleaseVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
    }

    /// <summary>The latest release as the licence portal reports it (GET /api/release/latest).</summary>
    public sealed record LatestRelease(
        [property: JsonPropertyName("version")] string Version,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("url")] string? Url,
        [property: JsonPropertyName("publishedAt")] DateTime? PublishedAt,
        [property: JsonPropertyName("security")] bool Security);

    /// <summary>What an update notice says, by edition: a Managed instance cannot be updated from
    /// inside it (Azure's deny assignment locks the customer out of the app's files), so it says who
    /// installs it; a self-hosted one points at the release.</summary>
    public static class UpdateNotice
    {
        /// <summary>Whether to notify: the release is newer than this server and was not already
        /// announced here.</summary>
        public static bool ShouldNotify(ReleaseVersion? current, ReleaseVersion? latest, string? lastNotified) =>
            current != null && latest != null && latest.CompareTo(current) > 0
            && !string.Equals(lastNotified, latest.ToString(), StringComparison.OrdinalIgnoreCase);

        /// <summary>The newer release this server was told about, if it still runs an older one:
        /// what the admin side column's line under the version shows.</summary>
        internal static (string Version, string? Url, bool Security)? Pending(InstanceSettings s, ReleaseVersion? current)
        {
            var announced = ReleaseVersion.Parse(s.LastAnnouncedRelease);
            if (current is null || announced is null || announced.CompareTo(current) <= 0) return null;
            return (announced.ToString(), s.LastAnnouncedReleaseUrl, s.LastAnnouncedReleaseSecurity);
        }

        public static (string Title, string Body) For(LatestRelease release, ReleaseVersion current, bool managed)
        {
            var version = ReleaseVersion.Parse(release.Version)?.ToString() ?? release.Version;
            var title = release.Security ? $"Indx {version}: a security update" : $"Indx {version} is available";
            var what = release.Security
                ? $"Indx {version} fixes a security issue. This instance runs {current}; install it soon."
                : $"Indx {version} is out. This instance runs {current}.";
            var how = managed
                ? "This instance is looked after through Azure, so it is updated by Indx: write to support@indx.co and we install it, with a backup first."
                : $"Download it from {release.Url ?? "https://github.com/indxSearch/Indx/releases"}; the release notes say what changed.";
            return (title, $"{what} {how}");
        }
    }

    /// <summary>
    /// Once a day, asks the licence portal which release is the latest and, when it is newer than
    /// this server, tells the admins once. A release is a notice in the console (the bell, and a line
    /// under the version in the admin side column), emailed only to admins who turned that on; a security release is also
    /// emailed by default.
    /// Anonymous: the request carries no token and nothing about the instance, so it works for every
    /// install, licensed or not, and tells the portal nothing. <c>Updates:Check</c> false switches
    /// it off.
    /// </summary>
    internal sealed class UpdateCheckJob(
        IConfiguration configuration,
        IHttpClientFactory httpClients,
        InstanceSettingsService settings,
        IEditionService edition,
        IServiceProvider services,
        ILogger<UpdateCheckJob> logger) : BackgroundService
    {
        public const string LatestReleaseUrl = "https://license.indx.co/api/release/latest";
        private static readonly TimeSpan FirstCheck = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!configuration.GetValue("Updates:Check", true)) return;
            var wait = FirstCheck;
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await Task.Delay(wait, stoppingToken); }
                catch (OperationCanceledException) { break; }
                wait = Interval;
                try { await CheckAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex) { logger.LogInformation("update check: {Message}", ex.Message); }
            }
        }

        internal async Task CheckAsync(CancellationToken ct)
        {
            var current = ReleaseVersion.Current;
            if (current is null) return;
            using var client = httpClients.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(20);
            var release = await client.GetFromJsonAsync<LatestRelease>(LatestReleaseUrl, ct);
            if (release is null) return;
            var latest = ReleaseVersion.Parse(release.Version);
            var s = settings.Load();
            if (!UpdateNotice.ShouldNotify(current, latest, s.LastAnnouncedRelease)) return;

            var (title, body) = UpdateNotice.For(release, current, edition.Edition == IndxEdition.Managed);
            using (var scope = services.CreateScope())
                await scope.ServiceProvider.GetRequiredService<NotificationService>()
                    .CreateForAdminsAsync(release.Security ? NotificationType.SecurityUpdate : NotificationType.UpdateAvailable,
                        title, body, release.Url);
            s.LastAnnouncedRelease = latest!.ToString();
            s.LastAnnouncedReleaseUrl = release.Url;
            s.LastAnnouncedReleaseSecurity = release.Security;
            settings.Save(s);
            logger.LogInformation("update check: {Latest} is available (this is {Current}); the admins were told", latest, current);
        }
    }
}
