using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Indx.Api;
using Indx.Http;
using IndxServer.Engine;

namespace IndxServer.Services.SearchOrAsk
{
    /// <summary>One filter the description asked for: a value (text, or true/false) or a number range,
    /// either end open. Field and value are the dataset's own, checked against what it holds.</summary>
    public sealed record FilterSuggestion(string Field, string? Value, double? Min, double? Max);

    /// <summary>An order the description asked for ("størst", "flest elever"): a Sortable field.</summary>
    public sealed record SortSuggestion(string Field, bool Descending);

    /// <summary>The filters, the words no filter covers (to search for, or empty), and an order if one
    /// was asked for.</summary>
    public sealed record FiltersResult(FilterSuggestion[] Filters, string Text, long Ms, string? Reason = null,
        SortSuggestion? Sort = null);

    public sealed record FiltersOutcome(IntentStatus Status, FiltersResult? Result = null, int RetryAfterSeconds = 0);

    /// <summary>
    /// A catalogue's second step: a visitor's description of what to list ("private videregående i
    /// bergen") becomes filters on the dataset's own fields (Notes/search-or-ask.md, "Filters from a
    /// description"). A model gets the filterable fields with their types and the values they hold,
    /// and the dataset's notes about them; whatever it returns is checked against those values, so a
    /// filter it invents is dropped, never searched with. The page shows the filters as chips the
    /// visitor can remove.
    /// </summary>
    public sealed partial class SearchOrAskService
    {
        /// <summary>A text field offers its values only up to this many; past it the field is left out,
        /// since a value the model cannot see it can only guess.</summary>
        private const int MaxValuesPerField = 500;

        private sealed record FilterField(string Name, string Type, List<string>? Values, double? Min, double? Max);

        /// <summary>What a description can become: the filter fields, and the fields it can sort by.
        /// <see cref="Readable"/> are the filterable text fields with too many values to list as a
        /// filter (a director, a writer, a company): not offered as filters, but read from every
        /// record found, so a question about them is answered. Until Oct 2026 they were dropped
        /// whole, and "who directed mad max?" was told the database gives no director.</summary>
        private sealed record FilterSchema(List<FilterField> Fields, List<string> Sortable, List<string> Readable)
        {
            public int Count => Fields.Count;
        }

        // (team\0dataset) -> the schema, the field roles it was read with, and when. Values change with
        // the data, not per keystroke: read again after ten minutes, or at once when a role changed.
        private readonly ConcurrentDictionary<string, (FilterSchema Schema, string Roles, long At)> _filterFields = new();

        public async Task<FiltersOutcome> FiltersAsync(string teamId, string dataSetName, IServerSearchEngine engine,
            string text, string clientAddress, CancellationToken ct, SearchOrAskSettings? trial = null)
        {
            if (!Available) return new FiltersOutcome(IntentStatus.Unavailable);
            var settings = trial ?? Settings(teamId, dataSetName);
            if (trial == null && !settings.Enabled) return new FiltersOutcome(IntentStatus.Disabled);

            var started = Stopwatch.GetTimestamp();
            long Ms() => (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var fields = FilterFields(teamId, dataSetName, engine);
            if (fields.Count == 0)
                return new FiltersOutcome(IntentStatus.Ok, new FiltersResult([], text, Ms(), "noFilterableFields"));

            using (var lease = _intentLimiter.AttemptAcquire(clientAddress))
                if (!lease.IsAcquired)
                    return new FiltersOutcome(IntentStatus.RateLimited, RetryAfterSeconds: RetryAfter(lease));
            if (!TryTakeBudget(teamId, dataSetName, settings.DailyCallBudget))
                return new FiltersOutcome(IntentStatus.BudgetSpent, RetryAfterSeconds: SecondsToUtcMidnight());

            try
            {
                var (filters, left, sort, cost) = await SuggestFiltersAsync(teamId, dataSetName, settings, fields, text, ct);
                // The call has no statistics row of its own; its cost goes with the day's budget.
                if (cost is > 0) RecordCost(teamId, dataSetName, settings.DailyCallBudget, cost.Value);
                return new FiltersOutcome(IntentStatus.Ok, new FiltersResult(filters, left, Ms(), Sort: sort));
            }
            catch (SearchOrAskModelException ex)
            {
                _logger.LogWarning("Search or ask filters failed for {Team}/{Dataset}: {Message}", teamId, dataSetName, ex.Message);
                return new FiltersOutcome(IntentStatus.Ok, new FiltersResult([], text, Ms(), $"failed:{ex.Status}"));
            }
            catch (HttpRequestException)
            {
                return new FiltersOutcome(IntentStatus.Ok, new FiltersResult([], text, Ms(), "failed:unreachable"));
            }
        }

        /// <summary>The filter call itself, without the limits (an ask has passed them already).</summary>
        private async Task<(FilterSuggestion[] Filters, string Text, SortSuggestion? Sort, double? Cost)> SuggestFiltersAsync(string teamId,
            string dataSetName, SearchOrAskSettings settings, FilterSchema schema, string text, CancellationToken ct)
        {
            var system = FilterPrompt(SiteDescription(teamId, dataSetName, settings), settings.FilterNotes, schema);
            var (content, cost) = await _models.CompleteAsync(Setup.FilterModel, system, text, ct);
            var (filters, left, sort) = ParseFilters(content, schema);
            return (filters, left, sort, cost);
        }

        /// <summary>The fields a filter may use: Filterable ones. A text field also needs to be
        /// Facetable, since its values come from the facets of an empty search; a number's range
        /// comes from them when it is facetable too.</summary>
        private FilterSchema FilterFields(string teamId, string dataSetName, IServerSearchEngine engine)
        {
            var key = $"{teamId}\0{dataSetName}";
            var all = engine.GetFieldList();
            var roles = string.Join('|', all.Select(f => $"{f.Name}:{(f.Filterable ? 'f' : '-')}{(f.Facetable ? 'a' : '-')}{(f.Sortable ? 's' : '-')}"));
            if (_filterFields.TryGetValue(key, out var cached) && cached.Roles == roles && Environment.TickCount64 - cached.At < 600_000)
                return cached.Schema;

            var list = all.Where(f => f.Filterable).ToList();
            Dictionary<string, KeyValuePair<string, int>[]>? facets = null;
            if (list.Any(f => f.Facetable))
            {
                var result = IndxServerInternalApi.Manager.Search(new QueryProxy
                {
                    Text = "",
                    EnableFacets = true,
                    MaxNumberOfRecordsToReturn = 1,
                }, dataSetName, teamId);
                facets = result.Facets;
            }

            var fields = new List<FilterField>();
            var readable = new List<string>();
            foreach (var f in list)
            {
                var values = facets != null && facets.TryGetValue(f.Name, out var v)
                    ? v.Where(kv => !string.IsNullOrWhiteSpace(kv.Key)).Select(kv => kv.Key).ToList()
                    : null;
                switch (f.Type)
                {
                    case JsonValueKind.True or JsonValueKind.False:
                        fields.Add(new FilterField(f.Name, "true/false", null, null, null));
                        break;
                    case JsonValueKind.Number:
                        var numbers = values?.Select(x => double.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : (double?)null)
                            .Where(d => d != null).Select(d => d!.Value).ToList();
                        fields.Add(new FilterField(f.Name, "number", null,
                            numbers is { Count: > 0 } ? numbers.Min() : null, numbers is { Count: > 0 } ? numbers.Max() : null));
                        break;
                    default:
                        if (values is { Count: > 0 and <= MaxValuesPerField })
                            fields.Add(new FilterField(f.Name, "text", values.OrderBy(x => x, StringComparer.CurrentCulture).ToList(), null, null));
                        else
                            readable.Add(f.Name);
                        break;
                }
            }
            var schema = new FilterSchema(fields, all.Where(f => f.Sortable).Select(f => f.Name).ToList(), readable);
            _filterFields[key] = (schema, roles, Environment.TickCount64);
            return schema;
        }

        /// <summary>Forgets a dataset's filter fields, after its field configuration changed.</summary>
        public void FilterFieldsChanged(string teamId, string dataSetName) => _filterFields.TryRemove($"{teamId}\0{dataSetName}", out _);

        private static string FilterPrompt(string site, string? notes, FilterSchema schema)
        {
            var fields = schema.Fields;
            var sb = new StringBuilder();
            sb.AppendLine("You turn what a visitor typed into a search field into filters on a dataset.");
            sb.AppendLine($"The dataset: {site}");
            sb.AppendLine();
            sb.AppendLine("The fields a filter may use:");
            foreach (var f in fields)
            {
                sb.Append($"- {f.Name} ({f.Type})");
                if (f.Values != null) sb.Append($": one of {string.Join(" | ", f.Values)}");
                else if (f.Min != null) sb.Append($": from {f.Min.Value.ToString(CultureInfo.InvariantCulture)} to {f.Max!.Value.ToString(CultureInfo.InvariantCulture)}");
                sb.AppendLine();
            }
            if (schema.Sortable.Count > 0)
                sb.AppendLine().AppendLine($"The fields a list can be sorted by: {string.Join(", ", schema.Sortable)}.");
            if (!string.IsNullOrWhiteSpace(notes)) sb.AppendLine().AppendLine(notes.Trim());
            sb.AppendLine("""

                Use only these fields, and for a field with listed values only those values, written exactly as
                listed (match what the visitor meant: a place name without its other-language part, a misspelling).
                A number filter is a range, inclusive, either end open. Use a filter only for something the visitor
                said; leave out what they did not ask for. A word the notes about the fields explain is always a filter,
                however many other things the visitor asked for, and never text. Whatever the visitor typed that no
                filter covers goes in "text" as words to search for; leave it empty when the filters cover it all.
                When the visitor asks for an order (the largest, the smallest, the most, sorted by), give "sort" with one
                of the sortable fields; otherwise leave it out.

                Answer with JSON only:
                {"filters": [{"field": "<field>", "value": "<listed value, or true/false>"} | {"field": "<field>", "min": <number>, "max": <number>}], "text": "<words left, or empty>", "sort": {"field": "<sortable field>", "descending": true}}
                """);
            return sb.ToString();
        }

        private static (FilterSuggestion[], string, SortSuggestion?) ParseFilters(string content, FilterSchema schema)
        {
            var fields = schema.Fields;
            // Some models fence their JSON despite being asked for JSON only: read the object out.
            int start = content.IndexOf('{'), end = content.LastIndexOf('}');
            if (start < 0 || end < start) return ([], "", null);
            JsonNode? root;
            try { root = JsonNode.Parse(content[start..(end + 1)]); }
            catch (JsonException) { return ([], "", null); }

            var filters = new List<FilterSuggestion>();
            foreach (var n in root?["filters"]?.AsArray() ?? [])
            {
                var field = fields.FirstOrDefault(f => f.Name == n?["field"]?.ToString());
                if (field == null) continue;
                var value = n!["value"]?.ToString();
                double? min = n["min"] is JsonValue a && a.TryGetValue<double>(out var x) ? x : null;
                double? max = n["max"] is JsonValue b && b.TryGetValue<double>(out var y) ? y : null;
                switch (field.Type)
                {
                    case "true/false" when value is "true" or "false" or "True" or "False":
                        filters.Add(new FilterSuggestion(field.Name, value.ToLowerInvariant(), null, null));
                        break;
                    case "number" when min != null || max != null:
                        filters.Add(new FilterSuggestion(field.Name, null, min, max));
                        break;
                    case "text" when value != null && field.Values!.FirstOrDefault(v => string.Equals(v, value, StringComparison.OrdinalIgnoreCase)) is { } listed:
                        filters.Add(new FilterSuggestion(field.Name, listed, null, null));
                        break;
                }
            }
            SortSuggestion? sort = null;
            if (root?["sort"]?["field"]?.ToString() is { } sortField && schema.Sortable.Contains(sortField))
                sort = new SortSuggestion(sortField, root["sort"]!["descending"] is JsonValue d && d.TryGetValue<bool>(out var desc) && desc);
            return (filters.ToArray(), root?["text"]?.ToString()?.Trim() ?? "", sort);
        }
    }
}
