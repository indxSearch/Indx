using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace IndxServer.Services
{
    /// <summary>The release the licence portal offers a Managed install (its catalog's "offered").</summary>
    public sealed record OfferedRelease(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("version")] string Version,
        [property: JsonPropertyName("publishedAt")] DateTime? PublishedAt);

    /// <summary>
    /// A Managed install's own updates (Notes/managed-upgrades.md). The customer cannot change the
    /// app (Azure's deny assignment), so the install asks the licence portal, with the token the
    /// portal wrote into it (Indx:ManagedToken, every plan; a paid install's licence token does as
    /// well): what is on offer, and to move to it. The portal then points the app's
    /// WEBSITE_RUN_FROM_PACKAGE at the release, and App Service restarts this app on it. A backup
    /// of the databases is taken first; the data stays where it is in any case.
    /// </summary>
    public sealed class ManagedUpdateClient(
        IConfiguration configuration,
        IHttpClientFactory httpClients,
        IEditionService edition,
        BackupService backups,
        ILogger<ManagedUpdateClient> logger)
    {
        public const string DefaultUrl = "https://license.indx.co/api/managed/update";

        private string Url => configuration["Indx:ManagedUpdateUrl"] is { Length: > 0 } u ? u : DefaultUrl;

        private string? Token =>
            configuration["Indx:ManagedToken"] is { Length: > 0 } t ? t
            : configuration["Indx:LicenseToken"] is { Length: > 0 } l ? l : null;

        /// <summary>Whether this install can update itself: a Managed install with its token.</summary>
        public bool CanUpdate => edition.Edition == IndxEdition.Managed && Token != null;

        /// <summary>What is on offer (null when nothing is), or why it could not be asked.</summary>
        public async Task<(OfferedRelease? Offered, string? Problem)> CheckAsync(CancellationToken ct = default)
        {
            if (!CanUpdate) return (null, "This instance is not a Managed App install with its token.");
            try
            {
                using var response = await SendAsync(HttpMethod.Get, null, ct);
                if (response.StatusCode == HttpStatusCode.Unauthorized) return (null, "Indx does not recognise this install's token.");
                response.EnsureSuccessStatusCode();
                var body = await response.Content.ReadFromJsonAsync<OfferBody>(ct);
                return (body?.Offered, null);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                logger.LogInformation("managed update check: {Message}", ex.Message);
                return (null, "Indx could not be reached. Try again in a moment.");
            }
        }

        /// <summary>Backs up the databases, then asks to move to <paramref name="release"/>. Null when
        /// the portal took it: the app restarts on the release within a minute or two.</summary>
        public async Task<string?> UpdateAsync(OfferedRelease release, CancellationToken ct = default)
        {
            if (!CanUpdate) return "This instance is not a Managed App install with its token.";
            var failed = (await backups.RunOnceAsync(ct)).FirstOrDefault(r => r.Error != null);
            if (failed != null) return $"The backup before the update failed ({failed.Database}: {failed.Error}), so nothing was changed.";
            try
            {
                using var response = await SendAsync(HttpMethod.Post, JsonContent.Create(new { release = release.Name }), ct);
                if (response.IsSuccessStatusCode)
                {
                    logger.LogInformation("managed update: asked to move to {Release}; the app restarts on it", release.Name);
                    return null;
                }
                var problem = await response.Content.ReadFromJsonAsync<ProblemBody>(ct);
                return problem?.Detail ?? $"Indx answered {(int)response.StatusCode}.";
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                return "Indx could not be reached, so nothing was changed. Try again in a moment.";
            }
        }

        private async Task<HttpResponseMessage> SendAsync(HttpMethod method, HttpContent? content, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(method, Url) { Content = content };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
            if (ReleaseVersion.Current is { } version) request.Headers.TryAddWithoutValidation("X-Indx-Version", version.ToString());
            var client = httpClients.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(30);
            return await client.SendAsync(request, ct);
        }

        private sealed record OfferBody([property: JsonPropertyName("offered")] OfferedRelease? Offered);
        private sealed record ProblemBody([property: JsonPropertyName("detail")] string? Detail);
    }
}
