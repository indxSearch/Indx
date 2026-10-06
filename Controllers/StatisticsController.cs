using Asp.Versioning;
using IndxServer.Models;
using IndxServer.Services;
using Microsoft.AspNetCore.Mvc;

namespace IndxServer.Controllers
{
    /// <summary>
    /// Reads and lifecycle for search statistics. Aggregates merge the rolled-up daily tables
    /// with the raw rows not yet rolled (the store does the merging). Deleting statistics is the
    /// EXPLICIT operation the design trades for survival across dataset delete/recreate — see
    /// Notes/statistics-design.md. Statistics disabled on the instance answers 404 problem+json
    /// with code <c>statisticsDisabled</c> on every route here.
    /// <para>The reads of searches, clicks and conversions take an optional <c>source</c>: the
    /// <c>?source=</c> the searches were sent with. With it they count that surface alone, read
    /// from the raw rows, so as far back as those are kept (90 days by default); a select or
    /// conversion counts for the surface of the search it names by queryId. A source no search
    /// was sent with gives zeros, not the totals. <c>statistics/sources</c> lists the surfaces.</para>
    /// </summary>
    public class StatisticsController(TeamContextResolver resolver, StatisticsService statistics)
        : DatasetApiController(resolver)
    {
        /// <summary>The window's totals and rates - the dashboard's header numbers. Rates are
        /// computed here so every consumer shares the definitions (see the response type).</summary>
        [KeyAccess(ApiKeyLevel.Read)]
        [HttpGet(DataSetRoute + "/statistics/overview")]
        public ActionResult<StatisticsOverviewResponse> Overview(string teamName, string dataSetName, int days = 30,
            string? source = null)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            if (Disabled(out var off)) return off!;
            var (fromDay, toDay) = Window(days);
            var o = statistics.Store!.Overview(ctx.OwnerKey, dataSetName, fromDay, toDay, Source(source));
            return new StatisticsOverviewResponse(
                o.Searches, o.ZeroHits, o.ClickedSearches, o.Selects, o.Converts, o.ConvertValueSum,
                Rate(o.ZeroHits, o.Searches), Rate(o.ClickedSearches, o.Searches),
                Rate(o.PositionSum, o.Selects), o.Uncovered, Rate(o.Uncovered, o.Searches),
                o.UncoveredChosen);
        }

        /// <summary>The per-day series behind the charts: one row per UTC day in the window.
        /// Days without events are served as zero rows, so a chart never has holes.</summary>
        [KeyAccess(ApiKeyLevel.Read)]
        [HttpGet(DataSetRoute + "/statistics/timeseries")]
        public ActionResult<StatisticsDayResponse[]> TimeSeries(string teamName, string dataSetName, int days = 30,
            string? source = null)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            if (Disabled(out var off)) return off!;
            var (fromDay, toDay) = Window(days);
            var byDay = statistics.Store!.TimeSeries(ctx.OwnerKey, dataSetName, fromDay, toDay, Source(source))
                .ToDictionary(r => r.Day);
            var series = new List<StatisticsDayResponse>((int)(toDay - fromDay + 1));
            for (long day = fromDay; day <= toDay; day++)
            {
                var r = byDay.GetValueOrDefault(day);
                series.Add(new StatisticsDayResponse(
                    DateOnly.FromDayNumber((int)day + DateOnly.Parse("1970-01-01").DayNumber).ToString("yyyy-MM-dd"),
                    r.Searches, r.ZeroHits, r.ClickedSearches, r.Selects, r.Converts,
                    r.ConvertValueSum, Rate(r.PositionSum, r.Selects), r.Uncovered));
            }
            return series.ToArray();
        }

        private static double? Rate(double numerator, double denominator) =>
            denominator == 0 ? null : numerator / denominator;

        /// <summary>Top queries in the window: searches, zero-hit count and selects per query
        /// text (lowercased). With uncoveredOnly=true, the searches without coverage: those coverage
        /// confirmed nothing for, how often a result was chosen anyway, and the document chosen most
        /// (mostChosenDocument), which is usually the synonym or spelling to add.
        /// order=lowestClickThrough lists the queries searched often and chosen from rarely
        /// (highestClickThrough the reverse; both leave out queries searched fewer than five
        /// times, where one click reads as 100%), and
        /// order=chosenAnyway with uncoveredOnly the fuzzy finds.
        /// With zeroHitsOnly=true, the zero-hit report — the searches the
        /// dataset could not answer, ordered by how often.</summary>
        [KeyAccess(ApiKeyLevel.Read)]
        [HttpGet(DataSetRoute + "/statistics/queries")]
        public ActionResult<QueryStat[]> Queries(string teamName, string dataSetName,
            int days = 30, int limit = 50, bool zeroHitsOnly = false, bool uncoveredOnly = false,
            QueryOrder order = QueryOrder.Searches, string? source = null)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            if (Disabled(out var off)) return off!;
            var (fromDay, toDay) = Window(days);
            return statistics.Store!.TopQueries(ctx.OwnerKey, dataSetName, fromDay, toDay,
                Math.Clamp(limit, 1, 1000), zeroHitsOnly, uncoveredOnly, order, Source(source)).ToArray();
        }

        /// <summary>For one query (<paramref name="text"/>, compared lowercased like the query
        /// list): which documents visitors chose from its results, most chosen first, with the
        /// position sum and the conversions from the same searches. A query searched often and
        /// chosen from rarely is either missing what people want or ranking it too low; which of
        /// the two, and whether the choices were the thing itself or a substitute, is read from
        /// this list. From the raw rows, so as far back as they are kept (90 days by default).</summary>
        [KeyAccess(ApiKeyLevel.Read)]
        [HttpGet(DataSetRoute + "/statistics/queries/documents")]
        public ActionResult<QueryDocumentStat[]> QueryDocuments(string teamName, string dataSetName,
            string? text, int days = 30, int limit = 50, string? source = null)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            if (Disabled(out var off)) return off!;
            if (string.IsNullOrWhiteSpace(text))
                return ApiProblems.InvalidArgument("text is required: the query whose chosen documents to list");
            var (fromDay, toDay) = Window(days);
            return statistics.Store!.QueryDocuments(ctx.OwnerKey, dataSetName, text, fromDay, toDay,
                Math.Clamp(limit, 1, 1000), Source(source)).ToArray();
        }

        /// <summary>Browsing in the window: how often people narrowed by each filter value, with
        /// or without text, and how often that came back empty. A combined filter counts once per
        /// operand; a range counts by its field, with an empty value.</summary>
        [KeyAccess(ApiKeyLevel.Read)]
        [HttpGet(DataSetRoute + "/statistics/filters")]
        public ActionResult<FilterStat[]> Filters(string teamName, string dataSetName,
            int days = 30, int limit = 50, string? source = null)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            if (Disabled(out var off)) return off!;
            var (fromDay, toDay) = Window(days);
            return statistics.Store!.TopFilters(ctx.OwnerKey, dataSetName, fromDay, toDay,
                Math.Clamp(limit, 1, 1000), Source(source)).ToArray();
        }

        /// <summary>Top documents in the window: selects, converts and summed convert value per
        /// document key. The keys are the customer's own; titles are looked up by key.</summary>
        [KeyAccess(ApiKeyLevel.Read)]
        [HttpGet(DataSetRoute + "/statistics/documents")]
        public ActionResult<DocumentStat[]> Documents(string teamName, string dataSetName,
            int days = 30, int limit = 50, string? source = null)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            if (Disabled(out var off)) return off!;
            var (fromDay, toDay) = Window(days);
            return statistics.Store!.TopDocuments(ctx.OwnerKey, dataSetName, fromDay, toDay,
                Math.Clamp(limit, 1, 1000), source: Source(source)).ToArray();
        }

        /// <summary>The surfaces searches came from in the window, most searched first: each
        /// <c>?source=</c> with its searches, counted as the overview counts them, and a row with
        /// a null source for the searches that named none. From the raw rows (90 days by default).</summary>
        [KeyAccess(ApiKeyLevel.Read)]
        [HttpGet(DataSetRoute + "/statistics/sources")]
        public ActionResult<SourceStat[]> Sources(string teamName, string dataSetName, int days = 30)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            if (Disabled(out var off)) return off!;
            var (fromDay, toDay) = Window(days);
            return statistics.Store!.Sources(ctx.OwnerKey, dataSetName, fromDay, toDay).ToArray();
        }

        /// <summary>One subject's lifetime top documents — the personalization read
        /// ("top 10 per user"). The subject is whatever the customer sent on the events.</summary>
        [KeyAccess(ApiKeyLevel.Read)]
        [HttpGet(DataSetRoute + "/statistics/subjects/{subject}")]
        public ActionResult<SubjectDocumentStat[]> Subject(string teamName, string dataSetName,
            string subject, int limit = 10)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            if (Disabled(out var off)) return off!;
            return statistics.Store!.TopForSubject(ctx.OwnerKey, dataSetName, subject,
                Math.Clamp(limit, 1, 1000)).ToArray();
        }

        /// <summary>What the dataset's owners changed in the window, oldest first, and the daily
        /// document totals: the why beside the numbers (Notes/statistics-design.md, "Change
        /// events").</summary>
        [KeyAccess(ApiKeyLevel.Read)]
        [HttpGet(DataSetRoute + "/statistics/changes")]
        public ActionResult<StatisticsChangesResponse> Changes(string teamName, string dataSetName, int days = 30)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            if (Disabled(out var off)) return off!;
            var (fromDay, toDay) = Window(days);
            var store = statistics.Store!;
            var changes = store.Changes(ctx.OwnerKey, dataSetName, fromDay, toDay)
                .Select(c => new StatisticsChangeResponse(
                    DateTimeOffset.FromUnixTimeMilliseconds(c.Timestamp).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    c.Kind,
                    c.Summary == null ? null : System.Text.Json.JsonDocument.Parse(c.Summary).RootElement.Clone()))
                .ToArray();
            var documents = store.DocumentChanges(ctx.OwnerKey, dataSetName, fromDay, toDay)
                .Select(d => new StatisticsDocumentChangesResponse(
                    DateOnly.FromDayNumber((int)d.Day + DateOnly.FromDateTime(DateTime.UnixEpoch).DayNumber).ToString("yyyy-MM-dd"),
                    d.Inserted, d.Updated, d.Deleted))
                .ToArray();
            return new StatisticsChangesResponse(changes, documents);
        }

        /// <summary>The dataset's statistics settings. <c>recordFilters</c>: whether each search's
        /// filter is stored, which is what the Browsing report counts. On by default.</summary>
        [KeyAccess(ApiKeyLevel.Read)]
        [HttpGet(DataSetRoute + "/statistics/settings")]
        public ActionResult<StatisticsSettings> GetSettings(string teamName, string dataSetName)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            if (Disabled(out var off)) return off!;
            return new StatisticsSettings(statistics.Store!.RecordsFilters(ctx.OwnerKey, dataSetName));
        }

        /// <summary>Changes the dataset's statistics settings. Switching <c>recordFilters</c> off
        /// stops recording filters; what is already recorded stays until it ages out or the
        /// statistics are erased. Team admin, Full key: a privacy setting.</summary>
        [KeyAccess(ApiKeyLevel.Full)]
        [HttpPut(DataSetRoute + "/statistics/settings")]
        public ActionResult<StatisticsSettings> PutSettings(string teamName, string dataSetName,
            [FromBody] StatisticsSettings settings)
        {
            var ctx = ResolveTeam(teamName, out var error, admin: true);
            if (ctx == null) return error!;
            if (Disabled(out var off)) return off!;
            statistics.Store!.SetRecordsFilters(ctx.OwnerKey, dataSetName, settings.RecordFilters);
            return new StatisticsSettings(statistics.Store.RecordsFilters(ctx.OwnerKey, dataSetName));
        }

        /// <summary>Deletes every statistics row of the dataset. This is the explicit purge:
        /// statistics survive dataset delete/recreate, so starting clean is a choice made here.</summary>
        [KeyAccess(ApiKeyLevel.Full)]
        [HttpDelete(DataSetRoute + "/statistics")]
        public IActionResult Purge(string teamName, string dataSetName)
        {
            var ctx = ResolveTeam(teamName, out var error, admin: true);
            if (ctx == null) return error!;
            if (Disabled(out var off)) return off!;
            statistics.Store!.PurgeDataset(ctx.OwnerKey, dataSetName);
            return NoContent();
        }

        /// <summary>The GDPR erasure of one subject: the subject↔document edge is deleted and the
        /// raw rows are anonymised, so aggregate counts stay true while nothing ties them to the
        /// subject any more.</summary>
        [KeyAccess(ApiKeyLevel.Full)]
        [HttpDelete(DataSetRoute + "/statistics/subjects/{subject}")]
        public IActionResult EraseSubject(string teamName, string dataSetName, string subject)
        {
            var ctx = ResolveTeam(teamName, out var error, admin: true);
            if (ctx == null) return error!;
            if (Disabled(out var off)) return off!;
            statistics.Store!.EraseSubject(ctx.OwnerKey, dataSetName, subject);
            return NoContent();
        }

        private bool Disabled(out ObjectResult? problem)
        {
            if (statistics.Enabled) { problem = null; return false; }
            problem = ApiProblems.Problem(StatusCodes.Status404NotFound, "statisticsDisabled",
                "Statistics disabled",
                "Statistics are switched off on this instance (Statistics:Enabled=false).");
            return true;
        }

        /// <summary>A source as the search stored it (trimmed, at most 40 characters), so a read
        /// finds what was written; blank means every surface.</summary>
        private static string? Source(string? source) => StatisticsService.CleanSource(source);

        private static (long FromDay, long ToDay) Window(int days)
        {
            long today = StatisticsStore.DayOf(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            return (today - Math.Clamp(days, 1, 3650) + 1, today);
        }
    }
}
