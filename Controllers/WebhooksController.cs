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
    public class WebhooksController(TeamContextResolver resolver, WebhookStore store, WebhookApplier applier, WebhookWorker worker)
        : DatasetApiController(resolver)
    {
        private const string Route = DataSetRoute + "/webhooks";

        /// <param name="Name">What it is for, shown in the console: "Sanity", "Price feed".</param>
        /// <param name="IdField">The field holding the sender's own id: text or a number. Default "id".</param>
        /// <param name="VersionField">A field that orders a document's versions (a date or a number):
        /// an event older than what is indexed is ignored. Optional.</param>
        /// <param name="BrakeShare">The share of the dataset that deletes and destructive updates may
        /// reach within an hour before a batch is held for an admin. Default 0.1.</param>
        /// <param name="Source">"indx" (default): the sender signs the Indx way. "sanity": a Sanity GROQ
        /// webhook calls it, and Indx fetches the full set from Sanity with <paramref name="Sanity"/>.</param>
        public sealed record CreateWebhookRequest(string? Name, string? IdField = null, string? VersionField = null, double? BrakeShare = null,
            string? Source = null, SanitySource? Sanity = null);

        /// <summary>A Sanity webhook's settings as listed: the token is never shown, only whether there is one.</summary>
        public sealed record SanityInfo(string ProjectId, string Dataset, string Query, string ApiVersion, int? SyncEveryHours, bool HasToken);

        /// <summary>A webhook as listed: everything but its secret, which is shown once.</summary>
        public sealed record WebhookInfo(string Id, string Name, string Url, string SyncUrl, string Source, string IdField, string? VersionField,
            double BrakeShare, DateTime CreatedUtc, DateTime? LastEventUtc, DateTime? LastSyncUtc, int Waiting, int Held, SanityInfo? Sanity);

        public sealed record CreatedWebhook(WebhookInfo Webhook, string Secret);

        /// <summary>
        /// Makes a webhook for the dataset. The answer carries its address and its secret, which is
        /// shown only here: the sender signs each call with it (X-Indx-Signature, or Sanity's own
        /// header). A dataset without documents yet is filled by the first full sync, which also
        /// gives it a key field; a Sanity webhook runs that sync at once.
        /// </summary>
        [HttpPost(Route)]
        public ActionResult<CreatedWebhook> Create(string teamName, string dataSetName, [FromBody] CreateWebhookRequest request)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true, admin: true);
            if (ctx == null) return error!;
            if (!FileNameValidity.IsValid(dataSetName)) return ApiProblems.InvalidDatasetName(dataSetName);
            if (IndxServerInternalApi.Manager.ResolveEngine(dataSetName, ctx.OwnerKey) is not { } engine)
                return ApiProblems.DatasetNotFound(dataSetName);
            var name = request?.Name?.Trim();
            if (string.IsNullOrEmpty(name) || name.Length > 80) return ApiProblems.InvalidArgument("Send { name } of 1 to 80 characters.");
            var source = string.IsNullOrWhiteSpace(request!.Source) ? WebhookSources.Indx : request.Source.Trim().ToLowerInvariant();
            if (!WebhookSources.IsKnown(source)) return ApiProblems.InvalidArgument("source is \"indx\" or \"sanity\".");
            string? sourceConfig = null;
            if (source == WebhookSources.Sanity)
            {
                if (request.Sanity is not { } sanity)
                    return ApiProblems.InvalidArgument("A Sanity webhook needs { sanity: { projectId, dataset, query } }.");
                sanity = sanity with { ApiVersion = string.IsNullOrWhiteSpace(sanity.ApiVersion) ? SanitySource.DefaultApiVersion : sanity.ApiVersion.Trim(),
                                       Token = string.IsNullOrWhiteSpace(sanity.Token) ? null : sanity.Token.Trim() };
                if (sanity.Problem() is { } problem) return ApiProblems.InvalidArgument(problem);
                sourceConfig = sanity.Serialize();
            }
            var idField = string.IsNullOrWhiteSpace(request.IdField) ? (source == WebhookSources.Sanity ? "_id" : "id") : request.IdField.Trim();
            var share = request.BrakeShare ?? 0.1;
            if (share is <= 0 or > 1) return ApiProblems.InvalidArgument("brakeShare is a share of the dataset, above 0 and at most 1.");
            var hook = store.Create(ctx.OwnerKey, dataSetName, name, idField, string.IsNullOrWhiteSpace(request.VersionField) ? null : request.VersionField.Trim(),
                share, source, sourceConfig);
            if (sourceConfig != null)
            {
                store.Enqueue(hook.Id, "", WebhookKinds.Sync);
                worker.Wake();
            }
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
            var secret = store.RotateSecret(webhookId)!;
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
            if (store.Release(webhookId, heldId) is not { } held)
                return ApiProblems.Problem(StatusCodes.Status404NotFound, "heldBatchNotFound", "No such held batch", $"No held batch {heldId}.");
            var outcome = held.Kind == WebhookKinds.Sync
                ? await applier.ApplySyncAsync(hook, held.Body, held.HeldUtc, pastBrake: true)
                : await applier.ApplyAsync(hook, held.Body, held.HeldUtc, pastBrake: true);
            if (outcome == WebhookOutcome.Retry)
            {
                store.Hold(webhookId, held.Reason, held.Events, held.Body, held.Kind);
                return ApiProblems.Problem(StatusCodes.Status409Conflict, "datasetBusy", "The dataset is busy",
                    "The dataset is loading or being rebuilt; the batch is held again. Try in a moment.");
            }
            return NoContent();
        }

        /// <summary>
        /// Fetches the full set from the webhook's source now and replaces the dataset's documents
        /// with it (a Sanity webhook). A webhook whose sender pushes its own sync is told where.
        /// </summary>
        [HttpPost(Route + "/{webhookId}/sync")]
        public IActionResult Sync(string teamName, string dataSetName, string webhookId)
        {
            var ctx = ResolveTeam(teamName, out var error, write: true, admin: true);
            if (ctx == null) return error!;
            if (Owned(ctx, dataSetName, webhookId) is not { } hook) return WebhookNotFound(webhookId);
            if (hook.SourceConfig == null)
                return ApiProblems.InvalidArgument($"This webhook's sender sends the full set itself, signed, to {HookUrl(hook)}/sync.");
            if (!store.SyncWaiting(hook.Id)) store.Enqueue(hook.Id, "", WebhookKinds.Sync);
            worker.Wake();
            return Accepted();
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

        private WebhookInfo Info(Webhook hook) => new(hook.Id, hook.Name, HookUrl(hook), HookUrl(hook) + "/sync", hook.Source,
            hook.IdField, hook.VersionField, hook.BrakeShare, hook.CreatedUtc, hook.LastEventUtc, hook.LastSyncUtc,
            store.WaitingCount(hook.Id), store.Held(hook.Id).Count,
            SanitySource.Parse(hook.SourceConfig) is { } s
                ? new SanityInfo(s.ProjectId, s.Dataset, s.Query, s.ApiVersion, s.SyncEveryHours, !string.IsNullOrEmpty(s.Token)) : null);
    }

    /// <summary>
    /// The address a sender calls: <c>POST /hooks/{webhookId}</c> with its events, and
    /// <c>POST /hooks/{webhookId}/sync</c> with the full set. No key: the signature, made with the
    /// webhook's secret (<c>X-Indx-Signature</c>, or Sanity's <c>sanity-webhook-signature</c>), is
    /// the authentication, and a call older than five minutes is refused. Accepted calls are stored
    /// before the answer (202) and applied in the order they came, so a sender never waits on the
    /// index and a restart loses nothing it was told was accepted.
    /// </summary>
    [ApiController]
    [AllowAnonymous]
    [Route("hooks")]
    public sealed class WebhookReceiverController(WebhookStore store, WebhookWorker worker) : ControllerBase
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
            if (Refuse(hook, body) is { } refused) return refused;
            if (Request.Headers.ContainsKey(SanitySignature.Header))
            {
                // A Sanity delivery: the operation and the document id come in headers.
                string? normalized;
                try
                {
                    normalized = SanitySignature.ToEvent(body, Request.Headers[SanitySignature.OperationHeader],
                        Request.Headers[SanitySignature.DocumentIdHeader], hook.IdField);
                }
                catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException)
                {
                    return ApiProblems.InvalidArgument(ex.Message);
                }
                if (normalized == null) return Accepted(); // a draft: nothing to index
                body = normalized;
            }
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
            if (Refuse(hook, body) is { } refused) return refused;
            if (string.IsNullOrWhiteSpace(body)) return ApiProblems.InvalidArgument("A sync carries the documents.");
            store.Enqueue(hook.Id, body, WebhookKinds.Sync);
            worker.Wake();
            return Accepted();
        }

        /// <summary>A bad signature, or too many calls; null when the call may go on.</summary>
        private ObjectResult? Refuse(Webhook hook, string body)
        {
            var now = DateTimeOffset.UtcNow;
            var problem = Request.Headers.ContainsKey(SanitySignature.Header)
                ? SanitySignature.Check(hook.Secret, Request.Headers[SanitySignature.Header], body, now)
                : WebhookSignature.Check(hook.Secret, Request.Headers[WebhookSignature.Header], body, now);
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
