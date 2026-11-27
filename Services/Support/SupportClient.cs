using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using IndxServer.Data;

namespace IndxServer.Services.Support
{
    /// <summary>Where the Portal is, and how often an instance with open tickets asks it for replies.</summary>
    public sealed class SupportOptions
    {
        public const string Section = "Indx:Support";
        /// <summary>The licence portal, which holds the tickets. Moved only for development.</summary>
        public string PortalUrl { get; set; } = "https://license.indx.co";
        /// <summary>Off hides the Support link and page, for an instance that must never call out.</summary>
        public bool Enabled { get; set; } = true;
        public int PollMinutes { get; set; } = 5;
        /// <summary>Where to write when the console cannot: shown on the page.</summary>
        public string Email { get; set; } = "support@indx.co";
    }

    public sealed record SupportTicketSummary(int Id, string Subject, string Kind, string Status, DateTime CreatedAt, DateTime UpdatedAt, bool Unread, string? WrittenBy);
    public sealed record SupportList(string Plan, string Promise, IReadOnlyList<SupportTicketSummary> Tickets);
    public sealed record SupportMessage(int Id, bool FromStaff, string AuthorName, string? AuthorEmail, string Body, DateTime CreatedAt);
    public sealed record SupportTicket(int Id, string Subject, string Kind, string Status, string Plan, DateTime CreatedAt, DateTime? ClosedAt,
        bool DiagnosticsSent, bool Unread, IReadOnlyList<SupportMessage> Messages);

    /// <summary>Why a call to the Portal gave nothing: the page says each in its own words.</summary>
    /// <summary>Why a call to Indx's support did not go through. NotAllowed: the licence account may
    /// not write (blocked, or a free account whose email is not confirmed), with the Portal's words.</summary>
    public enum SupportProblem { None, Off, NoToken, Unreachable, Unauthorized, NotFound, TooMany, Invalid, ClosedTooLong, NotAllowed }

    public sealed record SupportResult<T>(T? Value, SupportProblem Problem = SupportProblem.None, string? Message = null)
    {
        public bool Ok => Problem == SupportProblem.None;
    }

    /// <summary>
    /// The instance's side of support tickets: the Portal's
    /// /api/support, with the licence token the licence fetch and Observr already use as the
    /// bearer. The Portal decides whose tickets they are and the plan; this sends who wrote, and
    /// the diagnostics only when the writer ticked them. The instance only ever calls out: replies
    /// are fetched (SupportPollJob), nothing connects in.
    /// </summary>
    internal sealed class SupportClient(IHttpClientFactory http, SupportOptions options, InstanceSettingsService settings,
        IConfiguration configuration, ILogger<SupportClient> logger)
    {
        public const string HttpClientName = "IndxSupport";

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        /// <summary>The licence token, from the admin settings or the Indx:LicenseToken setting.</summary>
        public string? Token()
        {
            var token = settings.Load().LicenseToken;
            if (string.IsNullOrWhiteSpace(token)) token = configuration["Indx:LicenseToken"];
            return string.IsNullOrWhiteSpace(token) ? null : token.Trim();
        }

        public Task<SupportResult<SupportList>> ListAsync(CancellationToken ct = default) =>
            SendAsync<SupportList>(HttpMethod.Get, "tickets", null, ct);

        /// <summary>A thread. <paramref name="seen"/> when a person opens it, which marks the replies read.</summary>
        public Task<SupportResult<SupportTicket>> GetAsync(int id, bool seen, CancellationToken ct = default) =>
            SendAsync<SupportTicket>(HttpMethod.Get, $"tickets/{id}{(seen ? "?seen=true" : "")}", null, ct);

        /// <summary>A new ticket from the console. <paramref name="kind"/> is support, feature or feedback.</summary>
        public Task<SupportResult<SupportTicket>> CreateAsync(string kind, string subject, string body, ClaimsPrincipal author, ApplicationUser? user,
            JsonNode? diagnostics, CancellationToken ct = default) =>
            SendAsync<SupportTicket>(HttpMethod.Post, "tickets", new
            {
                kind, subject, body, author = Author(author, user), instanceVersion = IndxServer.Shared.AdminNav.VersionLine, diagnostics, source = "console",
            }, ct);

        /// <summary>A new ticket written by someone other than a signed-in person: Observr's draft,
        /// sent by an owner's Yes (<paramref name="source"/> "observr").</summary>
        public Task<SupportResult<SupportTicket>> CreateAsync(string kind, string subject, string body, string authorName, string? authorEmail,
            JsonNode? diagnostics, string source, CancellationToken ct = default) =>
            SendAsync<SupportTicket>(HttpMethod.Post, "tickets", new
            {
                kind, subject, body, author = new { name = authorName, email = authorEmail }, instanceVersion = IndxServer.Shared.AdminNav.VersionLine,
                diagnostics, source,
            }, ct);

        public Task<SupportResult<SupportTicket>> ReplyAsync(int id, string body, ClaimsPrincipal author, ApplicationUser? user, CancellationToken ct = default) =>
            SendAsync<SupportTicket>(HttpMethod.Post, $"tickets/{id}/messages", new { body, author = Author(author, user) }, ct);

        public async Task<SupportResult<bool>> CloseAsync(int id, CancellationToken ct = default)
        {
            var r = await SendAsync<JsonElement>(HttpMethod.Post, $"tickets/{id}/close", null, ct);
            return r.Ok ? new(true) : new(false, r.Problem, r.Message);
        }

        /// <summary>Who wrote: a console account is its email, which is also its name here.</summary>
        private static object Author(ClaimsPrincipal principal, ApplicationUser? user)
        {
            var email = user?.Email ?? principal.FindFirst(ClaimTypes.Email)?.Value ?? principal.Identity?.Name;
            return new { name = email ?? "Someone", email };
        }

        private async Task<SupportResult<T>> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
        {
            if (!options.Enabled) return new(default, SupportProblem.Off);
            if (Token() is not { } token) return new(default, SupportProblem.NoToken);
            using var request = new HttpRequestMessage(method, $"{options.PortalUrl.TrimEnd('/')}/api/support/{path}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (body != null) request.Content = JsonContent.Create(body, options: Json);
            try
            {
                using var response = await http.CreateClient(HttpClientName).SendAsync(request, ct);
                if (response.IsSuccessStatusCode)
                {
                    if (response.StatusCode == HttpStatusCode.NoContent || typeof(T) == typeof(JsonElement) && response.Content.Headers.ContentLength == 0)
                        return new(default);
                    return new(await response.Content.ReadFromJsonAsync<T>(Json, ct));
                }
                var message = await MessageOf(response, ct);
                return response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => new(default, SupportProblem.Unauthorized),
                    HttpStatusCode.NotFound => new(default, SupportProblem.NotFound),
                    HttpStatusCode.TooManyRequests => new(default, SupportProblem.TooMany, message),
                    HttpStatusCode.Conflict => new(default, SupportProblem.ClosedTooLong, message),
                    HttpStatusCode.Forbidden => new(default, SupportProblem.NotAllowed, message ?? "This license account may not write to support."),
                    HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge => new(default, SupportProblem.Invalid, message ?? "The Portal refused it as too large."),
                    _ => new(default, SupportProblem.Unreachable, $"The Portal answered {(int)response.StatusCode}."),
                };
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                logger.LogWarning("Support: the Portal could not be reached ({Reason})", ex.Message);
                return new(default, SupportProblem.Unreachable);
            }
        }

        private static async Task<string?> MessageOf(HttpResponseMessage response, CancellationToken ct)
        {
            try
            {
                var json = await response.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
                return json.TryGetProperty("message", out var m) ? m.GetString() : null;
            }
            catch (Exception) { return null; }
        }
    }
}
