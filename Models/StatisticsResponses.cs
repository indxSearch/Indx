namespace IndxServer.Models
{
    /// <summary>A dataset's statistics settings (GET/PUT …/statistics/settings).</summary>
    public record StatisticsSettings(bool RecordFilters);

    /// <summary>One change the dataset's owners made: when (UTC, ISO 8601), what kind
    /// (synonyms, boostRules, fields, reindex, replace, documents, hibernate, wake, rename,
    /// delete), and a small summary of counts and field names, never content or who.</summary>
    public record StatisticsChangeResponse(string At, string Kind, System.Text.Json.JsonElement? Summary);

    /// <summary>One day's document changes. Date is the UTC day as yyyy-MM-dd; days with none
    /// are left out.</summary>
    public record StatisticsDocumentChangesResponse(string Date, long Inserted, long Updated, long Deleted);

    /// <summary>What changed in the window, beside the numbers (GET …/statistics/changes).</summary>
    public record StatisticsChangesResponse(
        StatisticsChangeResponse[] Changes, StatisticsDocumentChangesResponse[] Documents);

    /// <summary>
    /// The dashboard's header numbers for a window. The rates are served computed so every
    /// consumer agrees on the definitions: ZeroHitRate = ZeroHits / Searches,
    /// ClickThroughRate = ClickedSearches / Searches (searches with at least one select, counted
    /// against ALL searches, zero-hit ones included — Algolia's definition), and
    /// AverageClickPosition = mean 1-based position of the selects that joined a search.
    /// Null rate/position means the denominator was zero, never "0%".
    /// </summary>
    public record StatisticsOverviewResponse(
        long Searches, long ZeroHits, long ClickedSearches, long Selects, long Converts,
        double ConvertValueSum, double? ZeroHitRate, double? ClickThroughRate,
        double? AverageClickPosition);

    /// <summary>One day of the chart series. Date is the UTC day as yyyy-MM-dd.</summary>
    public record StatisticsDayResponse(
        string Date, long Searches, long ZeroHits, long ClickedSearches, long Selects,
        long Converts, double ConvertValueSum, double? AverageClickPosition);
}
