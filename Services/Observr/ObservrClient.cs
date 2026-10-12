using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Indx.Api;
using Indx.Http;
using IndxServer.Engine;

namespace IndxServer.Services.Observr
{
    /// <summary>A record named for Observr: its key and the name the owner sees for it.</summary>
    public sealed record ObservrNamed(long Key, string Name);
    public sealed record ObservrWordMatches(string Word, List<ObservrNamed> Matches);

    /// <summary>A search that found nothing, with the evidence Observr judges it by. Gathered here,
    /// since only the instance has the index: the same search with strictness off, and what each
    /// word finds alone. The format is the contract with IndxObservr.</summary>
    public sealed record ObservrZeroHitAsk(string Kind, string Team, string Dataset, string Site, List<string> Fields,
        string Query, long Searches, List<ObservrNamed> NearMatches, List<ObservrWordMatches> Words);

    /// <summary>A proposal Observr made, which this instance can carry out after a yes.</summary>
    public sealed record ObservrProposal(string Kind, string[] Terms, string Direction, string Reason);
    public sealed record ObservrAnswer(int AskId, string Outcome, string Answer, List<ObservrProposal> Proposals, string Model);

    public enum ObservrStatus { Ok, NoToken, UnknownToken, NoSubscription, Unavailable, Failed }

    /// <summary>What came of asking: an answer, or why there is none, in a word the console turns
    /// into a sentence (<see cref="Reason"/> is Observr's own word where it gave one).</summary>
    public sealed record ObservrOutcome(ObservrStatus Status, ObservrAnswer? Answer = null, string? Reason = null);

    /// <summary>
    /// The instance's side of Observr: gathers what a question is about, sends it with the licence
    /// token, and carries out a proposal after the owner's yes through the same paths the console
    /// uses, so the change is recorded as any other. Observr never writes to an instance.
    /// </summary>
    internal sealed class ObservrClient(IHttpClientFactory http, ObservrOptions options, InstanceSettingsService settings,
        IConfiguration configuration, IDatasetEngines engines, DatasetMetadataStore metadata, StatisticsService statistics,
        ILogger<ObservrClient> logger)
    {
        public const string HttpClientName = "Observr";
        private const int NearMatches = 10;
        private const int PerWord = 3;
        private const int MaxWords = 8;

        /// <summary>Whether this instance can ask at all: it has a licence token.</summary>
        public bool HasToken => Token() != null;

        private string? Token()
        {
            var token = settings.Load().LicenseToken;
            if (string.IsNullOrWhiteSpace(token)) token = configuration["Indx:LicenseToken"];
            return string.IsNullOrWhiteSpace(token) ? null : token.Trim();
        }

        /// <summary>Asks Observr about a query that found nothing.</summary>
        public async Task<ObservrOutcome> AskZeroHitAsync(string teamId, string dataSetName, string query, long searches, CancellationToken ct)
        {
            if (Token() is not { } token) return new ObservrOutcome(ObservrStatus.NoToken);
            var engine = engines.FindSearchEngine(dataSetName, teamId);
            if (engine == null || engine.Status.SystemState != SystemState.Ready)
                return new ObservrOutcome(ObservrStatus.Failed, Reason: "datasetNotReady");

            var ask = BuildZeroHitAsk(teamId, dataSetName, engine, query, searches);
            var client = http.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{options.Url.TrimEnd('/')}/api/ask")
            {
                Content = JsonContent.Create(ask, options: new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            try
            {
                using var response = await client.SendAsync(request, ct);
                var body = await response.Content.ReadAsStringAsync(ct);
                string? reason = null;
                try { reason = JsonDocument.Parse(body).RootElement.TryGetProperty("reason", out var r) ? r.GetString() : null; }
                catch (JsonException) { }
                switch (response.StatusCode)
                {
                    case HttpStatusCode.OK:
                        var answer = JsonSerializer.Deserialize<ObservrAnswer>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                        return answer == null ? new ObservrOutcome(ObservrStatus.Failed, Reason: "badAnswer") : new ObservrOutcome(ObservrStatus.Ok, answer);
                    case HttpStatusCode.Unauthorized:
                        return new ObservrOutcome(ObservrStatus.UnknownToken);
                    case HttpStatusCode.PaymentRequired:
                        return new ObservrOutcome(ObservrStatus.NoSubscription, Reason: reason ?? "noSubscription");
                    case HttpStatusCode.ServiceUnavailable:
                        return new ObservrOutcome(ObservrStatus.Unavailable, Reason: reason ?? "unavailable");
                    default:
                        logger.LogWarning("Observr answered {Status} for {Team}/{Dataset}", (int)response.StatusCode, teamId, dataSetName);
                        return new ObservrOutcome(ObservrStatus.Failed, Reason: reason ?? $"http{(int)response.StatusCode}");
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                logger.LogWarning(ex, "Observr unreachable");
                return new ObservrOutcome(ObservrStatus.Unavailable, Reason: "unreachable");
            }
        }

        /// <summary>The evidence for a query that found nothing: the dataset's description and
        /// searchable fields, the same search with strictness off (pattern matches the visitor was
        /// not shown), and what each word finds alone. Searches here never reach the statistics.</summary>
        public ObservrZeroHitAsk BuildZeroHitAsk(string teamId, string dataSetName, IServerSearchEngine engine, string query, long searches)
        {
            var label = StatisticsLabels.For(engine, statistics.Store?.LabelField(teamId, dataSetName));
            List<ObservrNamed> Named(IEnumerable<long> keys) => keys.Select(k => new ObservrNamed(k, label(k) ?? k.ToString())).ToList();
            List<long> Keys(string text, int take, bool strict) =>
                (IndxServerInternalApi.Manager.Search(new QueryProxy
                {
                    Text = text,
                    MaxNumberOfRecordsToReturn = take,
                    EnableCoverage = true,
                    CoverageSetup = strict ? null : new CoverageSetupProxy { Truncate = false, IncludePatternMatches = true },
                }, dataSetName, teamId).Records ?? []).Select(r => r.DocumentKey).ToList();

            var near = Named(Keys(query, NearMatches, strict: false));
            var words = Regex.Split(query.Trim().ToLowerInvariant(), @"[^\p{L}\p{N}]+")
                .Where(w => w.Length >= 2).Distinct().Take(MaxWords)
                .Select(w => new ObservrWordMatches(w, Named(Keys(w, PerWord, strict: true)))).ToList();
            var fields = engine.GetFieldList().Where(f => f.Searchable).Select(f => f.Name).ToList();
            var site = metadata.Load(teamId, dataSetName);
            return new ObservrZeroHitAsk("zeroHitQuery", teamId, dataSetName, string.IsNullOrWhiteSpace(site) ? "" : site.Trim(),
                fields, query.Trim(), searches, near, words);
        }

        /// <summary>Carries out a synonym proposal: the terms as one entry in the dataset's list,
        /// through the same call the console's Synonyms tab makes, so the change is recorded. Does
        /// nothing when an entry already holds all the terms.</summary>
        public bool ApplySynonym(string teamId, string dataSetName, string[] terms)
        {
            var clean = terms.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (clean.Length < 2) return false;
            var list = engines.GetSynonyms(dataSetName, teamId) ?? new SynonymList();
            if (list.Entries.Any(e => clean.All(t => e.Terms.Contains(t, StringComparer.OrdinalIgnoreCase))))
                return true;
            list.Entries.Add(new SynonymEntry { Direction = SynonymDirection.Multidirectional, Terms = clean });
            list.Invalidate();
            return engines.SetSynonyms(dataSetName, teamId, list);
        }
    }
}
