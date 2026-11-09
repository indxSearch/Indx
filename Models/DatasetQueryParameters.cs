using Indx.Api;
using Indx.Http;

namespace IndxServer.Models
{
    /// <summary>
    /// A dataset's query parameters: the values a search takes when its request leaves them out.
    /// The same shape, names and meaning as those parts of a search body (6.0: <c>mode</c>,
    /// <c>match</c>, <c>candidates</c>, <c>ranking</c>), so a value can be copied between the two.
    /// Null means "not set here", and the engine default applies. Resolution, member by member:
    /// the request, then this, then the engine default (<see cref="Services.QueryParameterResolution"/>).
    /// A search's own values are not kept here: the cursor, the index terms, the field weights and
    /// boosts, the boosts and buries (<see cref="Services.QueryParameterResolution.Validate"/>).
    /// </summary>
    public sealed class DatasetQueryParameters
    {
        /// <summary>See <see cref="QueryProxy.Mode"/>.</summary>
        public SearchMode? Mode { get; set; }

        /// <summary>See <see cref="QueryProxy.Match"/>.</summary>
        public MatchProxy? Match { get; set; }

        /// <summary>See <see cref="QueryProxy.Candidates"/>.</summary>
        public CandidateProxy? Candidates { get; set; }

        /// <summary>See <see cref="QueryProxy.Ranking"/>.</summary>
        public RankingProxy? Ranking { get; set; }

        /// <summary>Focusing (9 Nov 2026, a prototype): when no record holds every word of a search,
        /// the list in BM25F's order rather than the fallback's (<see cref="Services.Focusing"/>).
        /// The dataset's alone, not a part of a search body: a request that names the mode is
        /// searched in it. Null and false are off.</summary>
        public bool? Focusing { get; set; }
    }

    /// <summary>GET …/query-parameters: what the dataset sets, and what a search that sends none of
    /// these values runs with.</summary>
    /// <param name="Parameters">The values the dataset sets; the rest are null.</param>
    /// <param name="Effective">Every value filled in: the dataset's where set, else the engine default.</param>
    public sealed record QueryParametersResponse(DatasetQueryParameters Parameters, DatasetQueryParameters Effective);
}
