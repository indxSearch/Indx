namespace IndxServer.Services.Webhooks
{
    /// <summary>What a team admin does to a dataset's webhooks, for the API (WebhooksController) and
    /// the console's Webhooks tab alike, so the two cannot drift. Callers check the role and that the
    /// webhook belongs to the dataset; this checks what was asked.</summary>
    public sealed class WebhookAdmin(WebhookStore store, WebhookApplier applier, WebhookWorker worker)
    {
        /// <summary>A webhook to make. <paramref name="Source"/> is "indx" (default) or "sanity", the
        /// latter with <paramref name="Sanity"/>; null fields take their defaults.</summary>
        public sealed record NewWebhook(string? Name, string? IdField = null, string? VersionField = null, double? BrakeShare = null,
            string? Source = null, SanitySource? Sanity = null);

        /// <summary>Makes the webhook, or says why not. A Sanity webhook fetches the full set at once.</summary>
        public (Webhook? Hook, string? Problem) Create(string teamId, string datasetName, NewWebhook request)
        {
            var name = request.Name?.Trim();
            if (string.IsNullOrEmpty(name) || name.Length > 80) return (null, "Send { name } of 1 to 80 characters.");
            var source = string.IsNullOrWhiteSpace(request.Source) ? WebhookSources.Indx : request.Source.Trim().ToLowerInvariant();
            if (!WebhookSources.IsKnown(source)) return (null, "source is \"indx\" or \"sanity\".");
            string? sourceConfig = null;
            if (source == WebhookSources.Sanity)
            {
                if (request.Sanity is not { } sanity)
                    return (null, "A Sanity webhook needs { sanity: { projectId, dataset, query } }.");
                sanity = sanity with { ApiVersion = string.IsNullOrWhiteSpace(sanity.ApiVersion) ? SanitySource.DefaultApiVersion : sanity.ApiVersion.Trim(),
                                       Token = string.IsNullOrWhiteSpace(sanity.Token) ? null : sanity.Token.Trim() };
                if (sanity.Problem() is { } problem) return (null, problem);
                sourceConfig = sanity.Serialize();
            }
            var idField = string.IsNullOrWhiteSpace(request.IdField) ? (source == WebhookSources.Sanity ? "_id" : "id") : request.IdField.Trim();
            var share = request.BrakeShare ?? 0.1;
            if (share is <= 0 or > 1) return (null, "brakeShare is a share of the dataset, above 0 and at most 1.");
            var hook = store.Create(teamId, datasetName, name, idField, string.IsNullOrWhiteSpace(request.VersionField) ? null : request.VersionField.Trim(),
                share, source, sourceConfig);
            if (sourceConfig != null) RequestSync(hook);
            return (hook, null);
        }

        /// <summary>Queues a fetch of the full set from the webhook's source, unless one already waits.
        /// False for a webhook whose sender sends the full set itself.</summary>
        public bool RequestSync(Webhook hook)
        {
            if (hook.SourceConfig == null) return false;
            if (!store.SyncWaiting(hook.Id)) store.Enqueue(hook.Id, "", WebhookKinds.Sync);
            worker.Wake();
            return true;
        }

        public enum HeldResult { Applied, NotFound, Busy }

        /// <summary>Applies a held batch as it came, past the brake. A busy dataset holds it again.</summary>
        public async Task<HeldResult> ApplyHeldAsync(Webhook hook, string heldId)
        {
            if (store.Release(hook.Id, heldId) is not { } held) return HeldResult.NotFound;
            var outcome = held.Kind == WebhookKinds.Sync
                ? await applier.ApplySyncAsync(hook, held.Body, held.HeldUtc, pastBrake: true)
                : await applier.ApplyAsync(hook, held.Body, held.HeldUtc, pastBrake: true);
            if (outcome != WebhookOutcome.Retry) return HeldResult.Applied;
            store.Hold(hook.Id, held.Reason, held.Events, held.Body, held.Kind);
            return HeldResult.Busy;
        }
    }
}
