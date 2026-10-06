namespace IndxServer.Models
{
    /// <summary>A dataset's statistics settings (GET …/statistics/settings, and the answer to a
    /// PUT). LabelField: the field the statistics name a document by; null means the first
    /// searchable field.</summary>
    public record StatisticsSettings(bool RecordFilters, string? LabelField = null);

    /// <summary>A change to the statistics settings (PUT …/statistics/settings). A property left
    /// out stays as it is; labelField "" goes back to the first searchable field.</summary>
    public record StatisticsSettingsUpdate(bool? RecordFilters = null, string? LabelField = null);

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
    ///
    /// <para>Uncovered counts the searches coverage confirmed nothing for: what was shown came
    /// from fuzzy matching alone, or nothing was shown. For a fuzzy engine that is the meaningful
    /// "found nothing", since it nearly always shows something; ZeroHits stays for compatibility.
    /// UncoveredRate = Uncovered / Searches.</para>
    ///
    /// <para>UncoveredChosen is the fuzzy finds: of those, the searches where a result was chosen
    /// anyway, a typo too large for an exact match that fuzzy search still answered.</para>
    /// </summary>
    public record StatisticsOverviewResponse(
        long Searches, long ZeroHits, long ClickedSearches, long Selects, long Converts,
        double ConvertValueSum, double? ZeroHitRate, double? ClickThroughRate,
        double? AverageClickPosition, long Uncovered = 0, double? UncoveredRate = null,
        long UncoveredChosen = 0);

    /// <summary>One day of the chart series. Date is the UTC day as yyyy-MM-dd.</summary>
    public record StatisticsDayResponse(
        string Date, long Searches, long ZeroHits, long ClickedSearches, long Selects,
        long Converts, double ConvertValueSum, double? AverageClickPosition, long Uncovered = 0);
}
