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
    /// </summary>
    public class StatisticsController(TeamContextResolver resolver, StatisticsService statistics)
        : DatasetApiController(resolver)
    {
        /// <summary>The window's totals and rates - the dashboard's header numbers. Rates are
        /// computed here so every consumer shares the definitions (see the response type).</summary>
        [KeyAccess(ApiKeyLevel.Read)]
        [HttpGet(DataSetRoute + "/statistics/overview")]
        public ActionResult<StatisticsOverviewResponse> Overview(string teamName, string dataSetName, int days = 30)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            if (Disabled(out var off)) return off!;
            var (fromDay, toDay) = Window(days);
            var o = statistics.Store!.Overview(ctx.OwnerKey, dataSetName, fromDay, toDay);
            return new StatisticsOverviewResponse(
                o.Searches, o.ZeroHits, o.ClickedSearches, o.Selects, o.Converts, o.ConvertValueSum,
                Rate(o.ZeroHits, o.Searches), Rate(o.ClickedSearches, o.Searches),
                Rate(o.PositionSum, o.Selects));
        }

        /// <summary>The per-day series behind the charts: one row per UTC day in the window.
        /// Days without events are served as zero rows, so a chart never has holes.</summary>
        [KeyAccess(ApiKeyLevel.Read)]
        [HttpGet(DataSetRoute + "/statistics/timeseries")]
        public ActionResult<StatisticsDayResponse[]> TimeSeries(string teamName, string dataSetName, int days = 30)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            if (Disabled(out var off)) return off!;
            var (fromDay, toDay) = Window(days);
            var byDay = statistics.Store!.TimeSeries(ctx.OwnerKey, dataSetName, fromDay, toDay)
                .ToDictionary(r => r.Day);
            var series = new List<StatisticsDayResponse>((int)(toDay - fromDay + 1));
            for (long day = fromDay; day <= toDay; day++)
            {
                var r = byDay.GetValueOrDefault(day);
                series.Add(new StatisticsDayResponse(
                    DateOnly.FromDayNumber((int)day + DateOnly.Parse("1970-01-01").DayNumber).ToString("yyyy-MM-dd"),
                    r.Searches, r.ZeroHits, r.ClickedSearches, r.Selects, r.Converts,
                    r.ConvertValueSum, Rate(r.PositionSum, r.Selects)));
            }
            return series.ToArray();
        }

        private static double? Rate(double numerator, double denominator) =>
            denominator == 0 ? null : numerator / denominator;

        /// <summary>Top queries in the window: searches, zero-hit count and selects per query
        /// text (lowercased). With zeroHitsOnly=true, the zero-hit report — the searches the
        /// dataset could not answer, ordered by how often.</summary>
        [KeyAccess(ApiKeyLevel.Read)]
        [HttpGet(DataSetRoute + "/statistics/queries")]
        public ActionResult<QueryStat[]> Queries(string teamName, string dataSetName,
            int days = 30, int limit = 50, bool zeroHitsOnly = false)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            if (Disabled(out var off)) return off!;
            var (fromDay, toDay) = Window(days);
            return statistics.Store!.TopQueries(ctx.OwnerKey, dataSetName, fromDay, toDay,
                Math.Clamp(limit, 1, 1000), zeroHitsOnly).ToArray();
        }

        /// <summary>Top documents in the window: selects, converts and summed convert value per
        /// document key. The keys are the customer's own; titles are looked up by key.</summary>
        [KeyAccess(ApiKeyLevel.Read)]
        [HttpGet(DataSetRoute + "/statistics/documents")]
        public ActionResult<DocumentStat[]> Documents(string teamName, string dataSetName,
            int days = 30, int limit = 50)
        {
            var ctx = ResolveTeam(teamName, out var error);
            if (ctx == null) return error!;
            if (Disabled(out var off)) return off!;
            var (fromDay, toDay) = Window(days);
            return statistics.Store!.TopDocuments(ctx.OwnerKey, dataSetName, fromDay, toDay,
                Math.Clamp(limit, 1, 1000)).ToArray();
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

        private static (long FromDay, long ToDay) Window(int days)
        {
            long today = StatisticsStore.DayOf(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            return (today - Math.Clamp(days, 1, 3650) + 1, today);
        }
    }
}
