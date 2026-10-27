namespace IndxServer.Services.Webhooks
{
    /// <summary>What a team admin does to a dataset's webhooks, for the API (WebhooksController) and
    /// the console's Webhooks tab alike, so the two cannot drift. Callers check the role and that the
    /// webhook belongs to the dataset; this checks what was asked.</summary>
    public sealed class WebhookAdmin(WebhookStore store, WebhookApplier applier, WebhookWorker worker, WebhookSourceRegistry sources)
    {
        /// <summary>A webhook to make. <paramref name="Source"/> is a source's id ("indx" by default),
        /// <paramref name="Settings"/> what that source takes; null fields take their defaults.</summary>
        public sealed record NewWebhook(string? Name, string? IdField = null, string? VersionField = null, double? BrakeShare = null,
            string? Source = null, System.Text.Json.Nodes.JsonObject? Settings = null);

        /// <summary>Makes the webhook, or says why not. A source Indx fetches from runs its first full sync at once.</summary>
        public (Webhook? Hook, string? Problem) Create(string teamId, string datasetName, NewWebhook request)
        {
            var name = request.Name?.Trim();
            if (string.IsNullOrEmpty(name) || name.Length > 80) return (null, "Give the webhook a name of 1 to 80 characters.");
            var source = string.IsNullOrWhiteSpace(request.Source) ? sources.Get(WebhookSources.Indx) : sources.Get(request.Source);
            if (source == null) return (null, $"source is one of {sources.Known}.");
            var (config, problem) = source.Configure(request.Settings);
            if (problem != null) return (null, problem);
            var idField = string.IsNullOrWhiteSpace(request.IdField) ? source.DefaultIdField : request.IdField.Trim();
            var share = request.BrakeShare ?? 0.1;
            if (share is <= 0 or > 1) return (null, "The brake is a share of the dataset, above 0 and at most 1 (a percent above 0 and at most 100).");
            var hook = store.Create(teamId, datasetName, name, idField, string.IsNullOrWhiteSpace(request.VersionField) ? null : request.VersionField.Trim(),
                share, source.Id, config?.ToJsonString(), source.NewSecret());
            if (source.Pulls) RequestSync(hook);
            return (hook, null);
        }

        /// <summary>A new secret in the source's form; the old one stops working at once. Null when the webhook is gone.</summary>
        public string? RotateSecret(Webhook hook) => store.RotateSecret(hook.Id, sources.For(hook).NewSecret());

        /// <summary>Queues a fetch of the full set from the webhook's source, unless one already waits.
        /// False for a webhook whose sender sends the full set itself.</summary>
        public bool RequestSync(Webhook hook)
        {
            if (!sources.For(hook).Pulls || hook.SourceConfig == null) return false;
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
