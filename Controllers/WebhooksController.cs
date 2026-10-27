using System.Collections.Concurrent;
using IndxServer.Engine;
using IndxServer.Models;
using IndxServer.Services;
using IndxServer.Services.Webhooks;
using Indx.Utilities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IndxServer.Controllers
{
    /// <summary>
    /// A dataset's incoming webhooks (Notes/indx_webhooks.md): a shop or a CMS calls one when
    /// something changes, and the documents follow without code of the customer's own. Team admins
    /// manage them; the call itself is signed with the webhook's secret instead of a key.
    /// </summary>
    public class WebhooksController(TeamContextResolver resolver, WebhookStore store, WebhookAdmin admin, WebhookSourceRegistry sources)
        : DatasetApiController(resolver)
    {
        private const string Route = DataSetRoute + "/webhooks";

        /// <param name="Name">What it is for, shown in the console: "Sanity", "Price feed".</param>
        /// <param name="IdField">The field holding the sender's own id: text or a number. Default "id".</param>
        /// <param name="VersionField">A field that orders a document's versions (a date or a number):
        /// an event older than what is indexed is ignored. Optional.</param>
        /// <param name="BrakeShare">The share of the dataset that deletes and destructive updates may
        /// reach within an hour before a batch is held for an admin. Default 0.1.</param>
        /// <param name="Source">Who calls it: "indx" (default), the sender signs the Indx way; "sanity" or
        /// "contentful", that system's own webhooks call it and Indx fetches the full set from it.</param>
        /// <param name="Settings">What the source takes: for "sanity" { projectId, dataset, query, token?,
        /// syncEveryHours? }, for "contentful" { spaceId, deliveryToken, environment?, contentType?, locale?,
        /// region?, syncEveryHours? }. GET …/webhooks/sources lists every source's settings.</param>
        public sealed record CreateWebhookRequest(string? Name, string? IdField = null, string? VersionField = null, double? BrakeShare = null,
            string? Source = null, System.Text.Json.Nodes.JsonObject? Settings = null);

        /// <summary>A webhook as listed: everything but its secret, which is shown once. <see cref="Settings"/>
        /// leaves out the secret ones (a token); <see cref="SecretSettings"/> names those that are set.</summary>
        public sealed record WebhookInfo(string Id, string Name, string Url, string SyncUrl, string Source, string IdField, string? VersionField,
            double BrakeShare, DateTime CreatedUtc, DateTime? LastEventUtc, DateTime? LastSyncUtc, int Waiting, int Held,
            System.Text.Json.Nodes.JsonObject? Settings, string[] SecretSettings);

        /// <summary>A source as offered: its id, what it takes and whether Indx fetches the full set from it.</summary>
        public sealed record SourceInfo(string Id, string Label, string Description, string DefaultIdField, bool Pulls, IReadOnlyList<SourceSetting> Settings);

        /// <summary>The sources a webhook can have, with their settings.</summary>
        [HttpGet(DataSetRoute + "/webhooks/sources")]
        public ActionResult<SourceInfo[]> Sources(string teamName, string dataSetName)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true, admin: true);
            if (ctx == null) return error!;
            return Ok(sources.All.Select(s => new SourceInfo(s.Id, s.Label, s.Description, s.DefaultIdField, s.Pulls, s.Settings)).ToArray());
        }

        public sealed record CreatedWebhook(WebhookInfo Webhook, string Secret);

        /// <summary>
        /// Makes a webhook for the dataset. The answer carries its address and its secret, which is
        /// shown only here: the sender authenticates each call with it, the way its source does. A
        /// dataset without documents yet is filled by the first full sync, which also gives it a key
        /// field; a webhook whose source Indx fetches from runs that sync at once.
        /// </summary>
        [HttpPost(Route)]
        public ActionResult<CreatedWebhook> Create(string teamName, string dataSetName, [FromBody] CreateWebhookRequest request)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true, admin: true);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName)) return ApiProblems.InvalidDatasetName(dataSetName);
            if (IndxServerInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey) is not { } engine)
                return ApiProblems.DatasetNotFound(dataSetName);
            if (request == null) return ApiProblems.InvalidArgument("Send { name } of 1 to 80 characters.");
            var (hook, problem) = admin.Create(ctx.OwnerKey, dataSetName, new WebhookAdmin.NewWebhook(request.Name, request.IdField,
                request.VersionField, request.BrakeShare, request.Source, request.Settings));
            if (hook == null) return ApiProblems.InvalidArgument(problem!);
            return Ok(new CreatedWebhook(Info(hook), hook.Secret));
        }

        /// <summary>The dataset's webhooks, with how many calls wait to be applied and how many batches the brake holds.</summary>
        [HttpGet(Route)]
        public ActionResult<WebhookInfo[]> List(string teamName, string dataSetName)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true, admin: true);
            if (ctx == null) return error!;
            return Ok(store.List(ctx.OwnerKey, dataSetName).Select(Info).ToArray());
        }

        [HttpDelete(Route + "/{webhookId}")]
        public IActionResult Delete(string teamName, string dataSetName, string webhookId)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true, admin: true);
            if (ctx == null) return error!;
            if (Owned(ctx, dataSetName, webhookId) is null) return WebhookNotFound(webhookId);
            store.Delete(webhookId);
            return NoContent();
        }

        /// <summary>A new secret, shown once; the old one stops working at once.</summary>
        [HttpPost(Route + "/{webhookId}/rotate-secret")]
        public ActionResult<CreatedWebhook> RotateSecret(string teamName, string dataSetName, string webhookId)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true, admin: true);
            if (ctx == null) return error!;
            if (Owned(ctx, dataSetName, webhookId) is null) return WebhookNotFound(webhookId);
            var secret = admin.RotateSecret(store.Get(webhookId)!)!;
            return Ok(new CreatedWebhook(Info(store.Get(webhookId)!), secret));
        }

        /// <summary>The latest calls, newest first: what each applied, ignored and refused, and why.</summary>
        [HttpGet(Route + "/{webhookId}/deliveries")]
        public ActionResult<WebhookDelivery[]> Deliveries(string teamName, string dataSetName, string webhookId, int limit = 50)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true, admin: true);
            if (ctx == null) return error!;
            if (Owned(ctx, dataSetName, webhookId) is null) return WebhookNotFound(webhookId);
            return Ok(store.Deliveries(webhookId, limit).ToArray());
        }

        public sealed record HeldInfo(string Id, DateTime HeldUtc, string Kind, string Reason, int Events);

        /// <summary>The batches the brake holds, waiting for an admin to apply or discard them.</summary>
        [HttpGet(Route + "/{webhookId}/held")]
        public ActionResult<HeldInfo[]> Held(string teamName, string dataSetName, string webhookId)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true, admin: true);
            if (ctx == null) return error!;
            if (Owned(ctx, dataSetName, webhookId) is null) return WebhookNotFound(webhookId);
            return Ok(store.Held(webhookId).Select(h => new HeldInfo(h.Id, h.HeldUtc, h.Kind, h.Reason, h.Events)).ToArray());
        }

        /// <summary>Applies a held batch as it came, past the brake.</summary>
        [HttpPost(Route + "/{webhookId}/held/{heldId}/apply")]
        public async Task<IActionResult> ApplyHeld(string teamName, string dataSetName, string webhookId, string heldId)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true, admin: true);
            if (ctx == null) return error!;
            if (Owned(ctx, dataSetName, webhookId) is not { } hook) return WebhookNotFound(webhookId);
            var result = await admin.ApplyHeldAsync(hook, heldId);
            if (result == WebhookAdmin.HeldResult.NotFound)
                return ApiProblems.Problem(StatusCodes.Status404NotFound, "heldBatchNotFound", "No such held batch", $"No held batch {heldId}.");
            if (result == WebhookAdmin.HeldResult.Busy)
                return ApiProblems.Problem(StatusCodes.Status409Conflict, "datasetBusy", "The dataset is busy",
                    "The dataset is loading or being rebuilt; the batch is held again. Try in a moment.");
            return NoContent();
        }

        /// <summary>
        /// Fetches the full set from the webhook's source now and replaces the dataset's documents
        /// with it (a Sanity or Contentful webhook). A webhook whose sender pushes its own sync is told where.
        /// </summary>
        [HttpPost(Route + "/{webhookId}/sync")]
        public IActionResult Sync(string teamName, string dataSetName, string webhookId)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true, admin: true);
            if (ctx == null) return error!;
            if (Owned(ctx, dataSetName, webhookId) is not { } hook) return WebhookNotFound(webhookId);
            if (!admin.RequestSync(hook))
                return ApiProblems.InvalidArgument($"This webhook's sender sends the full set itself, signed, to {HookUrl(hook)}/sync.");
            return Accepted();
        }

        /// <summary>Applies every held batch, oldest first. A busy dataset stops it (409); the
        /// batches not yet applied stay held, in their order.</summary>
        [HttpPost(Route + "/{webhookId}/held/apply")]
        public async Task<IActionResult> ApplyAllHeld(string teamName, string dataSetName, string webhookId)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true, admin: true);
            if (ctx == null) return error!;
            if (Owned(ctx, dataSetName, webhookId) is not { } hook) return WebhookNotFound(webhookId);
            var (_, busy) = await admin.ApplyAllHeldAsync(hook);
            return busy
                ? ApiProblems.Problem(StatusCodes.Status409Conflict, "datasetBusy", "The dataset is busy",
                    "The dataset is loading or being rebuilt; the batches not yet applied stay held. Try in a moment.")
                : NoContent();
        }

        /// <summary>Discards every held batch: nothing in them is applied.</summary>
        [HttpDelete(Route + "/{webhookId}/held")]
        public IActionResult DiscardAllHeld(string teamName, string dataSetName, string webhookId)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true, admin: true);
            if (ctx == null) return error!;
            if (Owned(ctx, dataSetName, webhookId) is not { } hook) return WebhookNotFound(webhookId);
            admin.DiscardAllHeld(hook);
            return NoContent();
        }

        /// <summary>Discards a held batch: nothing in it is applied.</summary>
        [HttpDelete(Route + "/{webhookId}/held/{heldId}")]
        public IActionResult DiscardHeld(string teamName, string dataSetName, string webhookId, string heldId)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true, admin: true);
            if (ctx == null) return error!;
            if (Owned(ctx, dataSetName, webhookId) is null) return WebhookNotFound(webhookId);
            return store.Release(webhookId, heldId) is null
                ? ApiProblems.Problem(StatusCodes.Status404NotFound, "heldBatchNotFound", "No such held batch", $"No held batch {heldId}.")
                : NoContent();
        }

        private Webhook? Owned(TeamContext ctx, string dataSetName, string webhookId) =>
            store.Get(webhookId) is { } hook && hook.TeamId == ctx.OwnerKey && hook.DatasetName == dataSetName ? hook : null;

        private static ObjectResult WebhookNotFound(string id) =>
            ApiProblems.Problem(StatusCodes.Status404NotFound, "webhookNotFound", "No such webhook", $"The dataset has no webhook {id}.");

        private string HookUrl(Webhook hook) => $"{Request.Scheme}://{Request.Host}/hooks/{hook.Id}";

        private WebhookInfo Info(Webhook hook)
        {
            var source = sources.For(hook);
            var config = SourceSettings.Parse(hook.SourceConfig);
            return new(hook.Id, hook.Name, HookUrl(hook), HookUrl(hook) + "/sync", hook.Source,
                hook.IdField, hook.VersionField, hook.BrakeShare, hook.CreatedUtc, hook.LastEventUtc, hook.LastSyncUtc,
                store.WaitingCount(hook.Id), store.Held(hook.Id).Count,
                SourceSettings.Public(source, config), SourceSettings.SecretsSet(source, config));
        }
    }

    /// <summary>
    /// The address a sender calls: <c>POST /hooks/{webhookId}</c> with its events, and
    /// <c>POST /hooks/{webhookId}/sync</c> with the full set. No key: the signature, made with the
    /// webhook's secret in the source's own way (<c>X-Indx-Signature</c>, Sanity's signature, Contentful's secret header), is
    /// the authentication, and a call older than five minutes is refused. Accepted calls are stored
    /// before the answer (202) and applied in the order they came, so a sender never waits on the
    /// index and a restart loses nothing it was told was accepted.
    /// </summary>
    [ApiController]
    [AllowAnonymous]
    [Route("hooks")]
    public sealed class WebhookReceiverController(WebhookStore store, WebhookWorker worker, WebhookSourceRegistry sources) : ControllerBase
    {
        public const int MaxBodyBytes = 5 * 1024 * 1024;
        public const int MaxSyncBytes = 256 * 1024 * 1024;
        /// <summary>Sanity sends one call per changed document, one at a time, so a bulk edit in the
        /// Studio arrives as a fast stream; the limit is set so that stream passes.</summary>
        public const int CallsPerMinute = 600;

        private static readonly ConcurrentDictionary<string, (long Minute, int Calls)> Rate = new();

        [HttpPost("{webhookId}")]
        [RequestSizeLimit(MaxBodyBytes)]
        public async Task<IActionResult> Receive(string webhookId)
        {
            if (store.Get(webhookId) is not { } hook)
                return ApiProblems.Problem(StatusCodes.Status404NotFound, "webhookNotFound", "No such webhook", "No webhook has this address.");
            using var reader = new StreamReader(Request.Body);
            var body = await reader.ReadToEndAsync(HttpContext.RequestAborted);
            var source = sources.For(hook);
            var call = Call(body);
            if (Refuse(hook, source.CheckCall(call, hook.Secret, DateTimeOffset.UtcNow)) is { } refused) return refused;
            string? events;
            try { events = source.ToEvents(call, hook); }
            catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException)
            {
                return ApiProblems.InvalidArgument(ex.Message);
            }
            if (events == null) return Accepted(); // a draft, an asset: nothing to index
            body = events;
            store.Enqueue(hook.Id, body);
            worker.Wake();
            return Accepted();
        }

        /// <summary>The full set, replacing the dataset's documents: a list of documents, or
        /// <c>{ "documents": [...] }</c>, signed like any call.</summary>
        [HttpPost("{webhookId}/sync")]
        [RequestSizeLimit(MaxSyncBytes)]
        public async Task<IActionResult> Sync(string webhookId)
        {
            if (store.Get(webhookId) is not { } hook)
                return ApiProblems.Problem(StatusCodes.Status404NotFound, "webhookNotFound", "No such webhook", "No webhook has this address.");
            using var reader = new StreamReader(Request.Body);
            var body = await reader.ReadToEndAsync(HttpContext.RequestAborted);
            // The full set pushed is the Indx scheme's, whatever the webhook's source.
            if (Refuse(hook, WebhookSignature.Check(hook.Secret, Request.Headers[WebhookSignature.Header], body, DateTimeOffset.UtcNow)) is { } refused)
                return refused;
            if (string.IsNullOrWhiteSpace(body)) return ApiProblems.InvalidArgument("A sync carries the documents.");
            store.Enqueue(hook.Id, body, WebhookKinds.Sync);
            worker.Wake();
            return Accepted();
        }

        /// <summary>The call as a source reads it: header names lowercase, the path as sent.</summary>
        private SourceCall Call(string body) => new(Request.Method, Request.Path.ToUriComponent() + Request.QueryString.ToUriComponent(),
            Request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => h.Value.ToString(), StringComparer.Ordinal), body);

        /// <summary>A bad signature, or too many calls; null when the call may go on.</summary>
        private ObjectResult? Refuse(Webhook hook, string? problem)
        {
            if (problem != null)
                return ApiProblems.Problem(StatusCodes.Status401Unauthorized, "badSignature", "The signature does not hold", problem);
            if (!Allow(hook.Id))
            {
                Response.Headers.RetryAfter = "60";
                return ApiProblems.Problem(StatusCodes.Status429TooManyRequests, "rateLimited", "Too many calls",
                    $"At most {CallsPerMinute} calls a minute per webhook; send events in batches (up to {WebhookPlanner.MaxEvents} a call).");
            }
            return null;
        }

        private static bool Allow(string webhookId)
        {
            var minute = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;
            var now = Rate.AddOrUpdate(webhookId, _ => (minute, 1), (_, v) => v.Minute == minute ? (minute, v.Calls + 1) : (minute, 1));
            return now.Calls <= CallsPerMinute;
        }
    }
}
