using System.Text.Json;
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
    public sealed class WebhookApplier(WebhookStore store, StatisticsService statistics, IServiceProvider services,
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

            if (!pastBrake && plan.Destructive > 0)
            {
                var recent = store.DestructiveSince(hook.TeamId, hook.DatasetName, DateTime.UtcNow.AddHours(-1));
                var limit = Math.Max(BrakeFloor, (int)Math.Ceiling(hook.BrakeShare * Math.Max(1, engine.Status.DocumentCount)));
                if (recent + plan.Destructive > limit)
                {
                    var reason = $"{plan.Destructive} deletes or destructive updates in this call, {recent} in the past hour; " +
                                 $"the brake allows {limit} an hour ({hook.BrakeShare:P0} of {engine.Status.DocumentCount:N0} documents).";
                    var held = store.Hold(hook.Id, reason, plan.Events, body);
                    Log(hook, receivedUtc, plan, "held", $"{reason} Held as {held.Id}: apply or discard it.");
                    await NotifyAsync(hook, "held", reason, always: true);
                    return WebhookOutcome.Held;
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
    public sealed class WebhookWorker(WebhookStore store, WebhookApplier applier, ILogger<WebhookWorker> logger) : BackgroundService
    {
        private readonly SemaphoreSlim _signal = new(0);

        public void Wake() { if (_signal.CurrentCount == 0) _signal.Release(); }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
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

        /// <summary>Applies what waits; true when something had to wait for its dataset.</summary>
        public async Task<bool> DrainAsync(CancellationToken ct)
        {
            var waiting = new HashSet<string>(); // a webhook whose call waits keeps its later calls behind it
            foreach (var item in store.Waiting(200))
            {
                ct.ThrowIfCancellationRequested();
                if (waiting.Contains(item.WebhookId)) continue;
                if (store.Get(item.WebhookId) is not { } hook) { store.Done(item.Seq); continue; }
                var outcome = await applier.ApplyAsync(hook, item.Body, item.ReceivedUtc);
                if (outcome == WebhookOutcome.Retry) { waiting.Add(hook.Id); continue; }
                store.Done(item.Seq);
            }
            return waiting.Count > 0;
        }
    }
}
