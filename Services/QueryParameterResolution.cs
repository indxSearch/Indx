using Indx.Api;
using Indx.Http;
using IndxServer.Models;

namespace IndxServer.Services
{
    /// <summary>
    /// Decides the coverage values a search runs with, one value at a time: what the request sent,
    /// else what the dataset's query parameters set, else the engine's default. A request that sends
    /// a value always wins, whatever the dataset says; a request that sends nothing gets the
    /// dataset's choices. The engine defaults are read from the library (<see cref="CoverageSetup"/>,
    /// <see cref="Query"/>) rather than repeated here, so a changed default reaches the server.
    /// </summary>
    public static class QueryParameterResolution
    {
        /// <summary>The engine's own coverage depth, used when neither request nor dataset sets one.</summary>
        public static int EngineCoverageDepth { get; } = new Query().CoverageDepth;

        /// <summary>The search response header naming the values the dataset supplied, comma separated.</summary>
        public const string ResponseHeader = "Indx-Query-Parameters";

        /// <summary>The largest coverage depth a dataset may set. The depth is how many candidates
        /// Coverage examines on every search, so a large one is paid for by every query.</summary>
        public const int MaxCoverageDepth = 100_000;

        /// <summary>The values a search runs with, and which of them came from the dataset.</summary>
        /// <param name="CoverageDepth">The depth the search runs with.</param>
        /// <param name="CoverageSetup">The complete setup the search runs with.</param>
        /// <param name="FromDataset">The JSON names of the values the dataset supplied, e.g.
        /// <c>coverageDepth</c>, <c>coverageSetup.truncate</c>. Empty when the dataset decided nothing.</param>
        public sealed record Resolved(int CoverageDepth, CoverageSetup CoverageSetup, IReadOnlyList<string> FromDataset);

        public static Resolved Resolve(QueryProxy request, DatasetQueryParameters? dataset)
        {
            var from = new List<string>();
            var r = request.CoverageSetup;
            var d = dataset?.CoverageSetup;
            var engine = new CoverageSetup();

            T Pick<T>(T? sent, T? set, T fallback, string name) where T : struct
            {
                if (sent.HasValue) return sent.Value;
                if (set.HasValue) { from.Add(name); return set.Value; }
                return fallback;
            }

            const string cs = "coverageSetup.";
            var depth = Pick(request.CoverageDepth, dataset?.CoverageDepth, EngineCoverageDepth, "coverageDepth");
            var setup = new CoverageSetup
            {
                IncludePatternMatches = Pick(r?.IncludePatternMatches, d?.IncludePatternMatches, engine.IncludePatternMatches, cs + "includePatternMatches"),
                LevenshteinMaxWordSize = Pick(r?.LevenshteinMaxWordSize, d?.LevenshteinMaxWordSize, engine.LevenshteinMaxWordSize, cs + "levenshteinMaxWordSize"),
                MinWordSize = Pick(r?.MinWordSize, d?.MinWordSize, engine.MinWordSize, cs + "minWordSize"),
                TruncateWordHitLimit = Pick(r?.TruncateWordHitLimit, d?.TruncateWordHitLimit, engine.TruncateWordHitLimit, cs + "truncateWordHitLimit"),
                TruncateWordHitTolerance = Pick(r?.TruncateWordHitTolerance, d?.TruncateWordHitTolerance, engine.TruncateWordHitTolerance, cs + "truncateWordHitTolerance"),
                CoverWholeQuery = Pick(r?.CoverWholeQuery, d?.CoverWholeQuery, engine.CoverWholeQuery, cs + "coverWholeQuery"),
                CoverWholeWords = Pick(r?.CoverWholeWords, d?.CoverWholeWords, engine.CoverWholeWords, cs + "coverWholeWords"),
                CoverFuzzyWords = Pick(r?.CoverFuzzyWords, d?.CoverFuzzyWords, engine.CoverFuzzyWords, cs + "coverFuzzyWords"),
                CoverJoinedWords = Pick(r?.CoverJoinedWords, d?.CoverJoinedWords, engine.CoverJoinedWords, cs + "coverJoinedWords"),
                CoverPrefixSuffix = Pick(r?.CoverPrefixSuffix, d?.CoverPrefixSuffix, engine.CoverPrefixSuffix, cs + "coverPrefixSuffix"),
                Truncate = Pick(r?.Truncate, d?.Truncate, engine.Truncate, cs + "truncate"),
                TruncationScore = Pick(r?.TruncationScore, d?.TruncationScore, engine.TruncationScore, cs + "truncationScore"),
            };
            return new Resolved(depth, setup, from);
        }

        /// <summary>What a search that sends no coverage values runs with on this dataset, every value
        /// filled in. The console shows it beside each setting; the API returns it as <c>effective</c>.</summary>
        public static DatasetQueryParameters Effective(DatasetQueryParameters? dataset)
        {
            var e = Resolve(new QueryProxy(), dataset);
            var s = e.CoverageSetup;
            return new DatasetQueryParameters
            {
                CoverageDepth = e.CoverageDepth,
                CoverageSetup = new CoverageSetupProxy
                {
                    IncludePatternMatches = s.IncludePatternMatches,
                    LevenshteinMaxWordSize = s.LevenshteinMaxWordSize,
                    MinWordSize = s.MinWordSize,
                    TruncateWordHitLimit = s.TruncateWordHitLimit,
                    TruncateWordHitTolerance = s.TruncateWordHitTolerance,
                    CoverWholeQuery = s.CoverWholeQuery,
                    CoverWholeWords = s.CoverWholeWords,
                    CoverFuzzyWords = s.CoverFuzzyWords,
                    CoverJoinedWords = s.CoverJoinedWords,
                    CoverPrefixSuffix = s.CoverPrefixSuffix,
                    Truncate = s.Truncate,
                    TruncationScore = s.TruncationScore,
                },
            };
        }

        /// <summary>The JSON names of the values a dataset sets, in the order <see cref="Resolve"/> reads them.</summary>
        public static IReadOnlyList<string> SetNames(DatasetQueryParameters? dataset) =>
            Resolve(new QueryProxy(), dataset).FromDataset;

        /// <summary>Why a dataset's query parameters cannot be saved, or null when they can. Only what
        /// the dataset sets is checked; a request's own values are passed to the engine as before.</summary>
        public static string? Validate(DatasetQueryParameters p)
        {
            if (p.CoverageDepth is { } depth && (depth < 1 || depth > MaxCoverageDepth))
                return $"coverageDepth must be between 1 and {MaxCoverageDepth:N0}.";
            var s = p.CoverageSetup;
            if (s == null) return null;
            if (s.LevenshteinMaxWordSize is { } l && (l < 1 || l > 63))
                return "coverageSetup.levenshteinMaxWordSize must be between 1 and 63.";
            if (s.MinWordSize is { } m && m < 1)
                return "coverageSetup.minWordSize must be at least 1.";
            if (s.TruncateWordHitLimit is { } limit && limit < 0)
                return "coverageSetup.truncateWordHitLimit cannot be negative.";
            if (s.TruncateWordHitTolerance is { } tolerance && tolerance < 0)
                return "coverageSetup.truncateWordHitTolerance cannot be negative.";
            return null;
        }
    }
}
