using System.Text.Json;
using Indx.Api;

namespace IndxServer.Components.Datasets
{
    /// <summary>
    /// Finds fields by name for the field configuration filter, the same way the synonyms tab
    /// filters its entries: a small Indx engine over the names, so a typo or a partial word still
    /// finds the field. A plain substring match is always included as well, because someone typing
    /// an exact fragment of a name must never be told it is not there.
    /// </summary>
    internal sealed class FieldNameFilter : IDisposable
    {
        private SearchEngine? _engine;
        private string[] _names = [];

        /// <summary>The names matching <paramref name="text"/>, as a set. Order is the table's
        /// business, since it groups nested fields under their containers.</summary>
        public HashSet<string> Match(IReadOnlyList<string> names, string text)
        {
            var matches = new HashSet<string>(StringComparer.Ordinal);
            var needle = text.Trim();
            if (needle.Length == 0) return matches;

            foreach (var name in names)
                if (name.Contains(needle, StringComparison.OrdinalIgnoreCase)) matches.Add(name);

            try
            {
                var engine = EngineFor(names);
                if (engine != null)
                {
                    var result = engine.Search(new Query { Text = needle, MaxNumberOfRecordsToReturn = Math.Max(50, names.Count) });
                    foreach (var r in result.Records)
                        if (r.Score > 0 && r.DocumentKey >= 0 && r.DocumentKey < _names.Length)
                            matches.Add(_names[(int)r.DocumentKey]);
                }
            }
            catch
            {
                // The fuzzy match is a convenience. The substring matches above still stand.
            }
            return matches;
        }

        /// <summary>The engine for this list of names, rebuilt when the list changes. A few dozen
        /// names index in milliseconds.</summary>
        private SearchEngine? EngineFor(IReadOnlyList<string> names)
        {
            if (_engine != null && _names.AsSpan().SequenceEqual(names.ToArray())) return _engine;
            Dispose();
            if (names.Count == 0) return null;

            _names = names.ToArray();
            // Dots and underscores become spaces so that "aspect" finds
            // cover.asset.metadata.dimensions.aspectRatio as a word of its own.
            var docs = _names.Select((n, i) => new { key = i, text = n + " " + n.Replace('.', ' ').Replace('_', ' ') });
            using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(docs)));

            var engine = new SearchEngine();
            var init = new ProcessMonitor();
            engine.Init(stream, "key", init);
            init.WaitForCompletion();
            foreach (var field in engine.GetFieldList())
                if (field.Name == "text") field.Searchable = true;
            stream.Position = 0;
            var load = new ProcessMonitor();
            engine.Load(stream, load);
            load.WaitForCompletion();
            var index = new ProcessMonitor();
            engine.Index(monitor: index);
            index.WaitForCompletion();

            _engine = engine;
            return engine;
        }

        public void Dispose()
        {
            _engine?.Dispose();
            _engine = null;
            _names = [];
        }
    }
}
