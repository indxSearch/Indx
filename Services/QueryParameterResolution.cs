using Indx.Api;
using Indx.Http;
using IndxServer.Models;

namespace IndxServer.Services
{
    /// <summary>
    /// Decides the values a search runs with, one value at a time: what the request sent, else what
    /// the dataset's query parameters set, else the engine's default. A request that sends a value
    /// always wins, whatever the dataset says; a request that sends nothing gets the dataset's
    /// choices. The engine defaults are read from the library (<see cref="MatchSetup"/>,
    /// <see cref="CandidateSetup"/>, <see cref="RankingSetup"/>, through the proxies' ToSetup)
    /// rather than repeated here, so a changed default reaches the server.
    /// </summary>
    public static class QueryParameterResolution
    {
        /// <summary>The search response header naming the values the dataset supplied, comma separated.</summary>
        public const string ResponseHeader = "Indx-Query-Parameters";

        /// <summary>The largest candidate bound a dataset may set. The bound is how many candidates
        /// every search ranks, so a large one is paid for by every query.</summary>
        public const int MaxCandidates = 100_000;

        /// <summary>The values a search runs with, and which of them came from the dataset.</summary>
        /// <param name="FromDataset">The JSON names of the values the dataset supplied, e.g.
        /// <c>mode</c>, <c>candidates.maxCandidates</c>. Empty when the dataset decided nothing.</param>
        public sealed record Resolved(SearchMode Mode, MatchSetup Match, CandidateSetup Candidates,
            RankingSetup Ranking, IReadOnlyList<string> FromDataset);

        public static Resolved Resolve(QueryProxy request, DatasetQueryParameters? dataset)
        {
            var from = new List<string>();
            void Note(bool sent, bool set, string name)
            {
                if (!sent && set) from.Add(name);
            }

            var rm = request.Match; var dm = dataset?.Match;
            var rc = request.Candidates; var dc = dataset?.Candidates;
            var rr = request.Ranking; var dr = dataset?.Ranking;

            Note(request.Mode != null, dataset?.Mode != null, "mode");
            Note(rm?.Tolerance != null, dm?.Tolerance != null, "match.tolerance");
            Note(rm?.Rungs != null, dm?.Rungs != null, "match.rungs");
            Note(rm?.OneEditFrom != null, dm?.OneEditFrom != null, "match.oneEditFrom");
            Note(rm?.TwoEditsFrom != null, dm?.TwoEditsFrom != null, "match.twoEditsFrom");
            Note(rm?.TypingPrefixFrom != null, dm?.TypingPrefixFrom != null, "match.typingPrefixFrom");
            Note(rm?.TypingPrefixOneEditFrom != null, dm?.TypingPrefixOneEditFrom != null, "match.typingPrefixOneEditFrom");
            Note(rm?.JoinedWords != null, dm?.JoinedWords != null, "match.joinedWords");
            Note(rc?.CandidatesPerRow != null, dc?.CandidatesPerRow != null, "candidates.candidatesPerRow");
            Note(rc?.MaxCandidates != null, dc?.MaxCandidates != null, "candidates.maxCandidates");
            Note(rc?.Fallback != null, dc?.Fallback != null, "candidates.fallback");
            Note(rc?.FillWithMostWords != null, dc?.FillWithMostWords != null, "candidates.fillWithMostWords");
            Note(rc?.FillReach != null, dc?.FillReach != null, "candidates.fillReach");
            Note(rc?.Rest != null, dc?.Rest != null, "candidates.rest");
            Note(rr?.Rerank != null, dr?.Rerank != null, "ranking.rerank");
            Note(rr?.Share != null, dr?.Share != null, "ranking.share");
            Note(rr?.TypingShift != null, dr?.TypingShift != null, "ranking.typingShift");
            Note(rr?.LongFieldFrom != null, dr?.LongFieldFrom != null, "ranking.longFieldFrom");
            Note(rr?.LongFieldWeightFloor != null, dr?.LongFieldWeightFloor != null, "ranking.longFieldWeightFloor");
            Note(rr?.WholeQueryFloor != null, dr?.WholeQueryFloor != null, "ranking.wholeQueryFloor");

            return new Resolved(
                request.Mode ?? dataset?.Mode ?? SearchMode.EditDistance,
                (rm ?? new MatchProxy()).ToSetup(dm),
                (rc ?? new CandidateProxy()).ToSetup(dc),
                (rr ?? new RankingProxy()).ToSetup(dr),
                from);
        }

        /// <summary>What a search that sends none of these values runs with on this dataset, every
        /// value filled in. The console shows it beside each setting; the API returns it as <c>effective</c>.</summary>
        public static DatasetQueryParameters Effective(DatasetQueryParameters? dataset)
        {
            var e = Resolve(new QueryProxy(), dataset);
            return new DatasetQueryParameters
            {
                Mode = e.Mode,
                Match = new MatchProxy
                {
                    Tolerance = e.Match.Tolerance,
                    Rungs = e.Match.Rungs,
                    OneEditFrom = e.Match.OneEditFrom,
                    TwoEditsFrom = e.Match.TwoEditsFrom,
                    TypingPrefixFrom = e.Match.TypingPrefixFrom,
                    TypingPrefixOneEditFrom = e.Match.TypingPrefixOneEditFrom,
                    JoinedWords = e.Match.JoinedWords,
                },
                Candidates = new CandidateProxy
                {
                    CandidatesPerRow = e.Candidates.CandidatesPerRow,
                    MaxCandidates = e.Candidates.MaxCandidates,
                    Fallback = e.Candidates.Fallback,
                    FillWithMostWords = e.Candidates.FillWithMostWords,
                    FillReach = e.Candidates.FillReach,
                    Rest = e.Candidates.Rest,
                },
                Ranking = new RankingProxy
                {
                    Rerank = e.Ranking.Rerank,
                    Share = e.Ranking.Share,
                    TypingShift = e.Ranking.TypingShift,
                    LongFieldFrom = e.Ranking.LongFieldFrom,
                    LongFieldWeightFloor = e.Ranking.LongFieldWeightFloor,
                    WholeQueryFloor = e.Ranking.WholeQueryFloor,
                },
            };
        }

        /// <summary>The JSON names of the values a dataset sets, in the order <see cref="Resolve"/> reads them.</summary>
        public static IReadOnlyList<string> SetNames(DatasetQueryParameters? dataset) =>
            Resolve(new QueryProxy(), dataset).FromDataset;

        /// <summary>Why a dataset's query parameters cannot be saved, or null when they can. Only what
        /// the dataset sets is checked; a request's own values are passed to the engine as before.
        /// A search's own values are refused: they are the search's, not the dataset's.</summary>
        public static string? Validate(DatasetQueryParameters p)
        {
            if (p.Match is { } m)
            {
                if (m.CursorAt != null)
                    return "match.cursorAt is a search's own: a dataset cannot set it.";
                foreach (var (name, value) in new[] { ("oneEditFrom", m.OneEditFrom), ("twoEditsFrom", m.TwoEditsFrom),
                    ("typingPrefixFrom", m.TypingPrefixFrom), ("typingPrefixOneEditFrom", m.TypingPrefixOneEditFrom) })
                    if (value is < 0 or > 64)
                        return $"match.{name} must be between 0 and 64.";
            }
            if (p.Candidates is { } c)
            {
                if (c.IndexTerms != null)
                    return "candidates.indexTerms is a search's own: a dataset cannot set it.";
                if (c.CandidatesPerRow is < 1 or > 1000)
                    return "candidates.candidatesPerRow must be between 1 and 1,000.";
                if (c.MaxCandidates is { } max && (max < 1 || max > MaxCandidates))
                    return $"candidates.maxCandidates must be between 1 and {MaxCandidates:N0}.";
            }
            if (p.Ranking is { } r)
            {
                if (r.FieldWeights != null || r.FieldBoosts != null)
                    return "ranking.fieldWeights and ranking.fieldBoosts are a search's own: a field's weight is set in the field configuration.";
                if (r.Boosts != null || r.Buries != null)
                    return "ranking.boosts and ranking.buries are a search's own: a dataset has its boost and bury rules.";
                if (r.Share is < 0 or > 1)
                    return "ranking.share must be between 0 and 1.";
                if (r.TypingShift is < 0 or > 1)
                    return "ranking.typingShift must be between 0 and 1.";
                if (r.LongFieldWeightFloor is < 0 or > 1)
                    return "ranking.longFieldWeightFloor must be between 0 and 1.";
                if (r.LongFieldFrom is < 0)
                    return "ranking.longFieldFrom cannot be negative.";
            }
            return null;
        }
    }
}
