using System.Text.Json;
using Indx.Api;

namespace IndxServer.Services
{
    /// <summary>
    /// A readable name for a document, beside its key: the value of one field. Which field is one
    /// rule for the statistics, Observr, the search preview and the category lists, so a record
    /// reads the same everywhere: the label field the dataset's owners chose
    /// (<see cref="StatisticsStore.LabelField"/>), else the title Search or ask is given, else the
    /// heaviest searchable field. Until Nov 2026 the fallback was the FIRST searchable field and
    /// the preview showed the first four: a dataset listing <c>authors</c> before <c>title</c>
    /// named every record without an author by its id (gyldendal.no, the Salaby products).
    /// <para>Read from the engine only when it is loaded: a statistics read never wakes a
    /// hibernated dataset (the console's rule, CLAUDE.md "The collector must never resolve an
    /// engine"). Asleep, every label is null and the key stands alone.</para>
    /// </summary>
    public static class StatisticsLabels
    {
        /// <summary>Longer values are cut here, with an ellipsis: a label names, it does not quote.</summary>
        public const int MaxLength = 120;

        /// <summary>How many values of an array field go into one label.</summary>
        private const int MaxArrayValues = 3;

        /// <summary>The field to read, from the engine's configuration. Null when there is none.</summary>
        public static string? ResolveField(IServerSearchEngine engine, string? configured, string? title = null) =>
            ResolveField(engine.GetFieldConfiguration() ?? [], configured, title);

        /// <summary>The field to read: the configured one when the dataset still has it, else the
        /// Search or ask title when it has that, else the heaviest searchable field (the first of
        /// equal weight, so it does not move between reads). Null when there is none of these.</summary>
        public static string? ResolveField(IReadOnlyList<FieldProxy> fields, string? configured, string? title = null)
        {
            if (!string.IsNullOrEmpty(configured) && fields.Any(f => f.FieldName == configured))
                return configured;
            if (!string.IsNullOrEmpty(title) && fields.Any(f => f.FieldName == title))
                return title;
            return fields.Where(f => (f.WordMatching == true || f.PatternMatching == true) && f.Embeddable != true)
                .OrderByDescending(f => f.Weight ?? 1f)
                .FirstOrDefault()?.FieldName;
        }

        /// <summary>
        /// A labeller for one read: key to label, null where there is none. Never throws; an
        /// engine that is not there, not loaded or gone while reading gives nulls.
        /// </summary>
        public static Func<long, string?> For(IServerSearchEngine? engine, string? configured, string? title = null)
        {
            try
            {
                if (engine == null || engine.IsDisposed) return _ => null;
                var field = ResolveField(engine, configured, title);
                if (field == null) return _ => null;
                var cache = new Dictionary<long, string?>();
                return key =>
                {
                    if (cache.TryGetValue(key, out var hit)) return hit;
                    string? label = null;
                    try
                    {
                        var json = engine.GetJsonDataOfKey(key);
                        if (!string.IsNullOrEmpty(json)) label = Read(json, field);
                    }
                    catch (Exception e) when (e is ObjectDisposedException or InvalidOperationException or JsonException) { }
                    return cache[key] = label;
                };
            }
            catch (Exception e) when (e is ObjectDisposedException or InvalidOperationException)
            {
                return _ => null;
            }
        }

        /// <summary>The value of <paramref name="field"/> in a document, by its dotted path
        /// ("author.name"); through an array, its first few values joined. Null when absent or
        /// empty.</summary>
        public static string? Read(string json, string field)
        {
            using var doc = JsonDocument.Parse(json);
            var values = new List<string>();
            Collect(doc.RootElement, field.Split('.'), 0, values);
            if (values.Count == 0) return null;
            var label = string.Join(", ", values.Take(MaxArrayValues));
            return label.Length <= MaxLength ? label : label[..MaxLength].TrimEnd() + "…";
        }

        private static void Collect(JsonElement e, string[] path, int i, List<string> into)
        {
            if (into.Count >= MaxArrayValues) return;
            if (e.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in e.EnumerateArray()) Collect(item, path, i, into);
                return;
            }
            if (i < path.Length)
            {
                if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(path[i], out var next))
                    Collect(next, path, i + 1, into);
                return;
            }
            var text = e.ValueKind switch
            {
                JsonValueKind.String => e.GetString(),
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => e.GetRawText(),
                _ => null,
            };
            if (!string.IsNullOrWhiteSpace(text)) into.Add(text.Trim());
        }
    }
}
