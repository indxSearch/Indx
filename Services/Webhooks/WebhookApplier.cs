using System.Text.Json;
using System.Text.Json.Nodes;
using Indx.Api;
using IndxServer.Data;
using IndxServer.Engine;
using IndxServer.Models;

namespace IndxServer.Services.Webhooks
{
    /// <summary>What applying a call came to: applied, held by the brake, refused whole, or not yet
    /// (the dataset is busy, try again shortly).</summary>
    public enum WebhookOutcome { Applied, Held, Refused, Retry }

    /// <summary>
    /// Applies a webhook call to its dataset: plans it (<see cref="WebhookPlanner"/>), weighs it
    /// against the brake, writes the documents, keeps the new keys, logs the delivery and counts it
    /// in the statistics. Used by the queue's worker and by an admin applying a held batch. A small
    /// call is applied to the live engine, as the single-document routes do; a large one on a shadow,
    /// as the bulk routes do, so a search is never held up by it.
    /// </summary>
    public sealed class WebhookApplier(WebhookStore store, StatisticsService statistics, WebhookSourceRegistry sources, IServiceProvider services,
        ILogger<WebhookApplier> logger)
    {
        /// <summary>Calls with more changes than this run on a shadow.</summary>
        public const int ShadowAbove = 50;
        /// <summary>The brake never trips below this many destructive changes in an hour: a small
        /// dataset loses a few documents in the normal course of things.</summary>
        public const int BrakeFloor = 20;

        private readonly Dictionary<string, DateTime> _notified = [];

        public async Task<WebhookOutcome> ApplyAsync(Webhook hook, string body, DateTime receivedUtc, bool pastBrake = false)
        {
            if (IndxServerInternalApi.Manager.ResolveEngine(hook.DatasetName, hook.TeamId) is not { } engine)
            {
                Log(hook, receivedUtc, null, "refused", "The dataset does not exist.");
                return WebhookOutcome.Refused;
            }
            var state = engine.Status.SystemState;
            if (state is SystemState.Loading or SystemState.Indexing) return WebhookOutcome.Retry;

            var plan = WebhookPlanner.Plan(body, hook, new EngineDataset(engine),
                ids => store.Keys(hook.TeamId, hook.DatasetName, ids),
                key => store.KeyTaken(hook.TeamId, hook.DatasetName, key));
            if (plan.Error != null)
            {
                Log(hook, receivedUtc, plan, "refused", plan.Error);
                await NotifyAsync(hook, "refused", plan.Error);
                return WebhookOutcome.Refused;
            }

            if (!pastBrake)
            {
                // While a batch is held the webhook waits: a later call joins it, so an admin decides
                // once for a bulk unpublish that came one document a call (Sanity, Contentful), and
                // nothing overtakes what waits (a delete held, then the same document republished).
                if (store.OpenHold(hook.Id) is { } open)
                {
                    if (open.Events + plan.Events > WebhookPlanner.MaxEvents)
                    {
                        var next = store.Hold(hook.Id, $"Waits behind the held batch {open.Id}, which holds as many changes as one call may.", plan.Events, body);
                        Log(hook, receivedUtc, plan, "held", $"Held as {next.Id}, behind {open.Id}.");
                        return WebhookOutcome.Held;
                    }
                    if (JoinHeld(open, body, plan.Events))
                    {
                        Log(hook, receivedUtc, plan, "held", $"Waits in the held batch {open.Id}, behind the changes held before it.");
                        return WebhookOutcome.Held;
                    }
                    // Gone: an admin applied or discarded it a moment ago. The call goes on as usual.
                }
                if (plan.Destructive > 0)
                {
                    var hourAgo = DateTime.UtcNow.AddHours(-1);
                    var recent = store.DestructiveSince(hook.TeamId, hook.DatasetName, hourAgo);
                    // Measured against the dataset as it was an hour ago, so the allowance does not
                    // shrink as the deletes it allows go through.
                    var size = Math.Max(1, engine.Status.DocumentCount + Math.Max(0, store.NetRemovedSince(hook.TeamId, hook.DatasetName, hourAgo)));
                    var limit = Math.Max(BrakeFloor, (int)Math.Ceiling(hook.BrakeShare * size));
                    if (recent + plan.Destructive > limit)
                    {
                        var reason = $"This call would bring the deletes and field-emptying updates within an hour to {recent + plan.Destructive:N0}; " +
                                     $"the brake allows {limit:N0} ({hook.BrakeShare:P0} of {size:N0} documents). Later calls wait with it.";
                        var held = store.Hold(hook.Id, reason, plan.Events, body);
                        Log(hook, receivedUtc, plan, "held", $"{reason} Held as {held.Id}: apply or discard it.");
                        await NotifyAsync(hook, "held", reason, always: true);
                        return WebhookOutcome.Held;
                    }
                }
            }

            int changes = plan.Inserts.Count + plan.Updates.Count + plan.Deletes.Count;
            string? failure = null;
            try
            {
                failure = changes > ShadowAbove && state == SystemState.Ready
                    ? IndxServerInternalApi.Manager.RunHeavyOnShadowIfReady(hook.DatasetName, hook.TeamId, e => Write(e, plan))
                    : Write(engine, plan);
            }
            catch (ShadowBusyException) { return WebhookOutcome.Retry; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Webhook {Hook} could not write to {Team}/{Dataset}", hook.Id, hook.TeamId, hook.DatasetName);
                failure = ex.Message;
            }
            if (failure != null)
            {
                Log(hook, receivedUtc, plan, "failed", failure);
                await NotifyAsync(hook, "failed", failure);
                return WebhookOutcome.Refused;
            }

            // Kept once written: a key the registry holds is a document that exists or did.
            store.SaveKeys(hook.TeamId, hook.DatasetName, plan.NewKeys);
            statistics.RecordDocuments(hook.TeamId, hook.DatasetName, "webhook", plan.Inserts.Count, plan.Updates.Count,
                plan.Deletes.Count, engine.Status.DocumentCount);
            Log(hook, receivedUtc, plan, "applied", Detail(plan));
            if (plan.Refusals.Count > 0)
                await NotifyAsync(hook, "refused", $"{plan.Refusals.Count} of {plan.Events} events refused, the first: {plan.Refusals[0]}");
            return WebhookOutcome.Applied;
        }

        /// <summary>Adds a call's changes to a held batch, after its own. False when the batch is gone.</summary>
        private bool JoinHeld(WebhookHeldBatch open, string body, int events)
        {
            var merged = new JsonArray();
            foreach (var e in WebhookPlanner.Events(JsonNode.Parse(open.Body))) merged.Add(e.DeepClone());
            foreach (var e in WebhookPlanner.Events(JsonNode.Parse(body))) merged.Add(e.DeepClone());
            return store.Join(open.Id, merged.ToJsonString(), open.Events + events);
        }

        /// <summary>
        /// Applies a full sync: the dataset's documents become the set it holds, on a shadow, so the
        /// old ones serve until the new are indexed. An empty body means "fetch it from the source"
        /// (a pulling webhook's requested or scheduled sync). The brake holds a sync that would leave
        /// the dataset smaller by more than its share, since a broken export looks just like that.
        /// A dataset with no key field gets the one the sync writes.
        /// </summary>
        public async Task<WebhookOutcome> ApplySyncAsync(Webhook hook, string body, DateTime receivedUtc, bool pastBrake = false)
        {
            if (IndxServerInternalApi.Manager.ResolveEngine(hook.DatasetName, hook.TeamId) is not { } engine)
            {
                LogSync(hook, receivedUtc, 0, 0, "refused", "The dataset does not exist.");
                return WebhookOutcome.Refused;
            }
            if (engine.Status.SystemState is SystemState.Loading or SystemState.Indexing) return WebhookOutcome.Retry;

            var source = sources.For(hook);
            if (string.IsNullOrEmpty(body))
            {
                if (!source.Pulls || SourceSettings.Parse(hook.SourceConfig) is not { } config)
                {
                    LogSync(hook, receivedUtc, 0, 0, "refused", "An empty sync, and no source to fetch the set from.");
                    return WebhookOutcome.Refused;
                }
                try { body = await source.FetchAllAsync(config); }
                catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
                {
                    LogSync(hook, receivedUtc, 0, 0, "failed", $"Fetching from {source.Label}: {ex.Message}");
                    await NotifyAsync(hook, "failed", $"Fetching from {source.Label}: {ex.Message}");
                    store.MarkSynced(hook.Id, DateTime.UtcNow); // tried: the schedule counts from here
                    return WebhookOutcome.Refused;
                }
            }

            var plan = WebhookPlanner.PlanSync(body, hook, new EngineDataset(engine),
                ids => store.Keys(hook.TeamId, hook.DatasetName, ids),
                key => store.KeyTaken(hook.TeamId, hook.DatasetName, key), source.LeavesOut);
            if (plan.Error == null && plan.Count == 0)
                plan.Error = "A sync with no documents: to empty the dataset, delete its documents instead.";
            if (plan.Error != null)
            {
                LogSync(hook, receivedUtc, plan.Count, plan.Refusals.Count, "refused", plan.Error);
                await NotifyAsync(hook, "refused", plan.Error);
                return WebhookOutcome.Refused;
            }

            var before = engine.Status.DocumentCount;
            var limit = Math.Max(BrakeFloor, (int)Math.Ceiling(hook.BrakeShare * before));
            if (!pastBrake && before - plan.Count > limit)
            {
                var reason = $"A full sync of {plan.Count:N0} documents would leave {before - plan.Count:N0} of {before:N0} out; " +
                             $"the brake allows {limit:N0} ({hook.BrakeShare:P0}).";
                var held = store.Hold(hook.Id, reason, plan.Count, body, WebhookKinds.Sync);
                LogSync(hook, receivedUtc, plan.Count, plan.Refusals.Count, "held", $"{reason} Held as {held.Id}: apply or discard it.");
                await NotifyAsync(hook, "held", reason, always: true);
                return WebhookOutcome.Held;
            }

            IndxServerInternalApi.Manager.DeclareKeyFieldForReplace(hook.DatasetName, hook.TeamId, plan.KeyField);
            ReplaceSchemaChange summary;
            try
            {
                using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(plan.Json));
                summary = await Task.Run(() => IndxServerInternalApi.Manager.RunReplaceFromJson(hook.DatasetName, hook.TeamId, stream,
                    fields => StartingRoles(fields, hook.IdField, plan.KeyField)));
            }
            catch (ShadowBusyException) { return WebhookOutcome.Retry; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Webhook {Hook} could not sync {Team}/{Dataset}", hook.Id, hook.TeamId, hook.DatasetName);
                LogSync(hook, receivedUtc, plan.Count, plan.Refusals.Count, "failed", ex.Message);
                await NotifyAsync(hook, "failed", ex.Message);
                return WebhookOutcome.Refused;
            }

            store.SaveKeys(hook.TeamId, hook.DatasetName, plan.NewKeys);
            store.MarkSynced(hook.Id, DateTime.UtcNow);
            var lines = new List<string> { $"{before:N0} documents before, {plan.Count:N0} now." };
            if (summary.LostRoles.Count > 0) lines.Add("Fields gone or retyped that had a role: " + string.Join(", ", summary.LostRoles));
            if (summary.Added.Count > 0) lines.Add("New fields: " + string.Join(", ", summary.Added.Take(20)));
            if (plan.Skipped > 0) lines.Add($"{plan.Skipped} unpublished documents left out.");
            lines.AddRange(plan.Refusals.Take(10));
            LogSync(hook, receivedUtc, plan.Count, plan.Refusals.Count, "synced", string.Join("\n", lines));
            if (summary.LostRoles.Count > 0)
                await NotifyAsync(hook, "refused", "The sync dropped or retyped fields with a role: " + string.Join(", ", summary.LostRoles));
            else if (plan.Refusals.Count > 0)
                await NotifyAsync(hook, "refused", $"{plan.Refusals.Count} documents left out of the sync, the first: {plan.Refusals[0]}");
            return WebhookOutcome.Applied;
        }

        /// <summary>
        /// The roles a webhook's first sync starts a dataset with, so it searches at once: text
        /// fields searchable, numbers and true/false filterable and sortable. Ids, the key and
        /// Sanity's system fields (_type, _rev, _createdAt...) get nothing. The console refines it.
        /// </summary>
        public static FieldProxy[] StartingRoles(FieldProxy[] fields, string idField, string keyField)
        {
            var roles = new List<FieldProxy>();
            foreach (var f in fields)
            {
                if (f.FieldName == idField || f.FieldName == keyField || f.FieldName.StartsWith('_')) continue;
                var type = f.FieldType ?? "";
                if (type.Equals(nameof(JsonValueKind.String), StringComparison.OrdinalIgnoreCase))
                    roles.Add(new FieldProxy { FieldName = f.FieldName, Searchable = true });
                else if (type.Equals(nameof(JsonValueKind.Number), StringComparison.OrdinalIgnoreCase)
                         || type.Equals(nameof(JsonValueKind.True), StringComparison.OrdinalIgnoreCase)
                         || type.Equals("Boolean", StringComparison.OrdinalIgnoreCase))
                    roles.Add(new FieldProxy { FieldName = f.FieldName, Filterable = true, Sortable = f.IsArray != true });
            }
            return [.. roles];
        }

        private void LogSync(Webhook hook, DateTime receivedUtc, int documents, int refused, string outcome, string? detail) =>
            store.Log(new WebhookDelivery(0, hook.Id, receivedUtc, documents, outcome == "synced" ? documents : 0, 0, 0, 0, refused, 0,
                outcome, detail is { Length: > 2000 } ? detail[..2000] + "…" : detail));

        /// <summary>Writes a plan; null when it all went in, else the engine's reason. Inserts on a
        /// dataset with nothing indexed yet load and index it, as the insert route does.</summary>
        private static string? Write(IServerSearchEngine engine, WebhookPlan plan)
        {
            if (plan.Inserts.Count > 0 && !engine.InsertJsonRecords(plan.Inserts.Select(i => i.Json), null, out var insertError))
                return insertError;
            if (plan.Updates.Count > 0 && !engine.UpdateJsonRecords(plan.Updates.Select(u => u.Json), null, out var updateError))
                return updateError;
            foreach (var key in plan.Deletes) engine.DeleteJsonRecord(key);
            return null;
        }

        private void Log(Webhook hook, DateTime receivedUtc, WebhookPlan? plan, string outcome, string? detail) =>
            store.Log(new WebhookDelivery(0, hook.Id, receivedUtc, plan?.Events ?? 0,
                outcome == "applied" ? plan!.Inserts.Count : 0, outcome == "applied" ? plan!.Updates.Count : 0,
                outcome == "applied" ? plan!.Deletes.Count : 0, plan?.Ignored ?? 0, plan?.Refusals.Count ?? 0,
                outcome == "applied" ? plan!.Destructive : 0, outcome, detail is { Length: > 2000 } ? detail[..2000] + "…" : detail));

        private static string? Detail(WebhookPlan plan)
        {
            var lines = plan.Refusals.Take(10).Concat(plan.Notes.Take(10)).ToList();
            return lines.Count == 0 ? null : string.Join("\n", lines);
        }

        /// <summary>The instance's admins hear of a held batch every time, and of refusals or a failure
        /// at most once an hour per webhook, so a sender stuck on a bad document does not flood them.</summary>
        private async Task NotifyAsync(Webhook hook, string what, string detail, bool always = false)
        {
            lock (_notified)
            {
                var key = $"{hook.Id}:{what}";
                if (!always && _notified.TryGetValue(key, out var last) && DateTime.UtcNow - last < TimeSpan.FromHours(1)) return;
                _notified[key] = DateTime.UtcNow;
            }
            var title = what switch
            {
                "held" => $"A webhook batch for {hook.DatasetName} is held",
                "failed" => $"A webhook for {hook.DatasetName} could not write",
                _ => $"A webhook for {hook.DatasetName} refused events",
            };
            var body = what == "held"
                ? $"{hook.Name}: {detail} Apply or discard it on the dataset's Webhooks."
                : $"{hook.Name}: {detail}";
            try
            {
                using var scope = services.CreateScope();
                await scope.ServiceProvider.GetRequiredService<NotificationService>()
                    .CreateForAdminsAsync(NotificationType.WebhookAttention, title, body);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not notify about webhook {Hook}", hook.Id);
            }
        }

        /// <summary>The engine as the planner reads it.</summary>
        private sealed class EngineDataset(IServerSearchEngine engine) : IWebhookDataset
        {
            public string KeyField => engine.DocumentFields?.NameOfDocumentKeyField ?? "";
            public IReadOnlyDictionary<string, JsonValueKind> FieldTypes { get; } =
                engine.DocumentFields == null ? new Dictionary<string, JsonValueKind>()
                : engine.GetFieldList().GroupBy(f => f.Name).ToDictionary(g => g.Key, g => g.First().Type);
            public long DocumentCount => engine.Status.DocumentCount;
            public string? Document(long key)
            {
                var json = engine.GetJsonDataOfKey(key);
                return string.IsNullOrEmpty(json) ? null : json;
            }
        }
    }

    /// <summary>
    /// Drains the webhook queue in the order calls came: each is applied, held or refused, and
    /// leaves the queue then; one the dataset cannot take yet (it is loading or a shadow is being
    /// built) waits and is tried again shortly. Woken at once when a call arrives.
    /// </summary>
    public sealed class WebhookWorker(WebhookStore store, WebhookApplier applier, WebhookSourceRegistry sources, ILogger<WebhookWorker> logger) : BackgroundService
    {
        private readonly SemaphoreSlim _signal = new(0);

        public void Wake() { if (_signal.CurrentCount == 0) _signal.Release(); }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    ScheduleSyncs(DateTime.UtcNow);
                    var retry = await DrainAsync(stoppingToken);
                    await _signal.WaitAsync(retry ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(30), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    logger.LogError(ex, "The webhook queue stopped on an error; it resumes in a few seconds");
                    try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); } catch (OperationCanceledException) { break; }
                }
            }
        }

        /// <summary>Queues a sync for every pulling webhook whose interval has passed (or that has
        /// never synced), unless one waits already. Returns how many were queued.</summary>
        public int ScheduleSyncs(DateTime nowUtc)
        {
            int queued = 0;
            foreach (var hook in store.Pulling())
            {
                if (!sources.For(hook).Pulls || SourceSettings.Parse(hook.SourceConfig) is not { } config) continue;
                var due = hook.LastSyncUtc is not { } last || SourceSettings.Hours(config) is { } hours && nowUtc - last >= TimeSpan.FromHours(hours);
                if (!due || store.SyncWaiting(hook.Id)) continue;
                store.Enqueue(hook.Id, "", WebhookKinds.Sync);
                queued++;
            }
            return queued;
        }

        /// <summary>Applies what waits; true when something had to wait for its dataset.</summary>
        public async Task<bool> DrainAsync(CancellationToken ct)
        {
            var waiting = new HashSet<string>(); // a webhook whose call waits keeps its later calls behind it
            foreach (var item in store.Waiting(200))
            {
                ct.ThrowIfCancellationRequested();
                if (waiting.Contains(item.WebhookId)) continue;
                if (store.Get(item.WebhookId) is not { } hook) { store.Done(item.Seq); continue; }
                var outcome = item.Kind == WebhookKinds.Sync
                    ? await applier.ApplySyncAsync(hook, item.Body, item.ReceivedUtc)
                    : await applier.ApplyAsync(hook, item.Body, item.ReceivedUtc);
                if (outcome == WebhookOutcome.Retry) { waiting.Add(hook.Id); continue; }
                store.Done(item.Seq);
            }
            return waiting.Count > 0;
        }
    }
}
