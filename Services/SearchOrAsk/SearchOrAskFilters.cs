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

    /// <summary>The filters, and the words no filter covers (to search for, or empty).</summary>
    public sealed record FiltersResult(FilterSuggestion[] Filters, string Text, long Ms, string? Reason = null);

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

        // (team\0dataset) -> the fields and when they were read. Values change with the data, not per
        // keystroke: read again after ten minutes.
        private readonly ConcurrentDictionary<string, (List<FilterField> Fields, long At)> _filterFields = new();

        public async Task<FiltersOutcome> FiltersAsync(string teamId, string dataSetName, IServerSearchEngine engine,
            string text, string clientAddress, CancellationToken ct, SearchOrAskSettings? trial = null)
        {
            if (!_options.Available) return new FiltersOutcome(IntentStatus.Unavailable);
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

            var system = FilterPrompt(SiteDescription(teamId, dataSetName, settings), settings.FilterNotes, fields);
            try
            {
                var (content, _) = await _models.CompleteAsync(_options.FilterModel, system, text, ct);
                var (filters, left) = ParseFilters(content, fields);
                return new FiltersOutcome(IntentStatus.Ok, new FiltersResult(filters, left, Ms()));
            }
            catch (SearchOrAskModelException ex)
            {
                _logger.LogWarning("Search or ask filters failed for {Team}/{Dataset}: {Status}", teamId, dataSetName, ex.Status);
                return new FiltersOutcome(IntentStatus.Ok, new FiltersResult([], text, Ms(), $"failed:{ex.Status}"));
            }
            catch (HttpRequestException)
            {
                return new FiltersOutcome(IntentStatus.Ok, new FiltersResult([], text, Ms(), "failed:unreachable"));
            }
        }

        /// <summary>The fields a filter may use: Filterable ones. A text field also needs to be
        /// Facetable, since its values come from the facets of an empty search; a number's range
        /// comes from them when it is facetable too.</summary>
        private List<FilterField> FilterFields(string teamId, string dataSetName, IServerSearchEngine engine)
        {
            var key = $"{teamId}\0{dataSetName}";
            if (_filterFields.TryGetValue(key, out var cached) && Environment.TickCount64 - cached.At < 600_000)
                return cached.Fields;

            var list = engine.GetFieldList().Where(f => f.Filterable).ToList();
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
                        break;
                }
            }
            _filterFields[key] = (fields, Environment.TickCount64);
            return fields;
        }

        /// <summary>Forgets a dataset's filter fields, after its field configuration changed.</summary>
        public void FilterFieldsChanged(string teamId, string dataSetName) => _filterFields.TryRemove($"{teamId}\0{dataSetName}", out _);

        private static string FilterPrompt(string site, string? notes, List<FilterField> fields)
        {
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
            if (!string.IsNullOrWhiteSpace(notes)) sb.AppendLine().AppendLine(notes.Trim());
            sb.AppendLine("""

                Use only these fields, and for a field with listed values only those values, written exactly as
                listed (match what the visitor meant: a place name without its other-language part, a misspelling).
                A number filter is a range, inclusive, either end open. Use a filter only for something the visitor
                said; leave out what they did not ask for. A word the notes about the fields explain is always a filter,
                however many other things the visitor asked for, and never text. Whatever the visitor typed that no
                filter covers goes in "text" as words to search for; leave it empty when the filters cover it all.

                Answer with JSON only:
                {"filters": [{"field": "<field>", "value": "<listed value, or true/false>"} | {"field": "<field>", "min": <number>, "max": <number>}], "text": "<words left, or empty>"}
                """);
            return sb.ToString();
        }

        private static (FilterSuggestion[], string) ParseFilters(string content, List<FilterField> fields)
        {
            // Some models fence their JSON despite being asked for JSON only: read the object out.
            int start = content.IndexOf('{'), end = content.LastIndexOf('}');
            if (start < 0 || end < start) return ([], "");
            JsonNode? root;
            try { root = JsonNode.Parse(content[start..(end + 1)]); }
            catch (JsonException) { return ([], ""); }

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
            return (filters.ToArray(), root?["text"]?.ToString()?.Trim() ?? "");
        }
    }
}
