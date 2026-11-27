using Indx.Http;

namespace IndxServer.Models
{
    /// <summary>
    /// A search as the server's search routes take it: the library's <see cref="QueryProxy"/>, with
    /// what only the server does (12 Nov 2026). A subclass, not new members on
    /// QueryProxy, because QueryProxy ships in IndxSearchLib and these mean nothing to the library.
    /// </summary>
    public class ServerQueryProxy : QueryProxy
    {
        /// <summary>
        /// With <c>includeDocuments</c>: only these top-level fields of each record in
        /// <c>documents</c>, in the record's own order; a field a record lacks is left out of it.
        /// Null or empty: the whole record. Holds every field of <c>collapseBy</c>, or the search is
        /// refused.
        /// </summary>
        public string[]? IncludeFields { get; set; }
    }

    /// <summary>
    /// One search of <c>search/multi</c>: a search with its own say in the statistics, over the
    /// query string's, so a page can send its visitor's search beside its helper searches in one
    /// call and count only the first.
    /// </summary>
    public sealed class MultiSearchItem : ServerQueryProxy
    {
        /// <summary>Whether this search is a visitor's, as <c>?count=</c>; null: the query string's.</summary>
        public bool? Count { get; set; }

        /// <summary>The surface this search came from, as <c>?source=</c>; null: the query string's.</summary>
        public string? Source { get; set; }

        /// <summary>The page-load session, as <c>?session=</c>; null: the query string's.</summary>
        public string? Session { get; set; }

        /// <summary>Who searched, as <c>?actor=</c>: <c>agent</c> for an AI agent; null: the query string's.</summary>
        public string? Actor { get; set; }
    }

    /// <summary>
    /// The body of <c>search/groups</c>: one text and its settings, split into named sections, each
    /// with its own filter, rows and count: a search box's books, authors and events in one call.
    /// The members of the search itself are every group's; a group's own filter is ANDed with the
    /// search's, and its own candidates, ranking and match replace the search's.
    /// </summary>
    public sealed class GroupedSearchRequest : ServerQueryProxy
    {
        /// <summary>The sections, answered by name. One to <c>MaxSearchesPerCall</c>.</summary>
        public SearchGroup[]? Groups { get; set; }
    }

    /// <summary>One section of a <see cref="GroupedSearchRequest"/>.</summary>
    public sealed class SearchGroup
    {
        /// <summary>The section's name, its key in the answer's <c>groups</c>. Unique in the request.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>A filter expression for the section (<c>contentType = "event"</c>), ANDed with
        /// the search's own. Null: the search's filter alone.</summary>
        public string? Filter { get; set; }

        /// <summary>How many records the section lists. 0 lists none and still counts.</summary>
        public int Rows { get; set; }

        /// <summary>A filter expression to count by instead of <see cref="Filter"/>: list the
        /// upcoming events, count them all. Only the answer's <c>count</c> is this one's.</summary>
        public string? CountFilter { get; set; }

        /// <summary>The section's own candidates (the fallback on for books, off for the rest); null:
        /// the search's. Replaces the search's whole, not member by member.</summary>
        public CandidateProxy? Candidates { get; set; }

        /// <summary>The section's own ranking (a boost for books only); null: the search's.</summary>
        public RankingProxy? Ranking { get; set; }

        /// <summary>The section's own match; null: the search's.</summary>
        public MatchProxy? Match { get; set; }

        /// <summary>The section that is the visitor's search: recorded in the statistics, its
        /// <c>queryId</c> the request's. One at most; none marked: the first.</summary>
        public bool Primary { get; set; }
    }

    /// <summary>The body of <c>facets/{field}/search</c>: what a visitor typed into a facet's own
    /// search field ("Search actors…"), how many values to answer, and the search the facet belongs
    /// to, so the counts are of its records.</summary>
    public sealed class FacetValueSearchRequest
    {
        /// <summary>What was typed. Empty: the field's commonest values.</summary>
        public string? Text { get; set; }

        /// <summary>How many values to answer, 1 to <c>MaxFacetValueRows</c>. Default 10.</summary>
        public int Rows { get; set; } = 10;

        /// <summary>The search the facet belongs to: the values it finds are counted in its records,
        /// and those it holds none of left out. Null: counted over every record.</summary>
        public ServerQueryProxy? Search { get; set; }

        /// <summary>Match among these values only (at most <c>FacetValueIndex.AmongLimit</c>): a panel
        /// that holds its facet's values and counts for the visitor's search sends them, and the server
        /// need not count that search again on every keystroke.</summary>
        public string[]? Values { get; set; }
    }
}
