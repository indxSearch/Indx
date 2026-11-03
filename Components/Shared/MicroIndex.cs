using System.Text.Json;
using Indx.Api;
using Indx.Systm.Blazor;

namespace IndxServer.Components.Shared
{
    /// <summary>
    /// A tiny Indx engine over a list of short texts, for the console's own pickers: the command
    /// palette's pages, a Combobox's options. Each text is a record keyed by its position, its one
    /// searchable field the text itself. A few hundred index in milliseconds, so it is built when
    /// first searched and thrown away when the list changes. It holds native memory: dispose it.
    /// </summary>
    internal static class MicroIndex
    {
        public static SearchEngine Build(IEnumerable<string> texts)
        {
            var docs = texts.Select((text, i) => new { key = i, text });
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
            return engine;
        }

        /// <summary>The positions an engine finds for the text, best first.</summary>
        public static IEnumerable<int> Find(SearchEngine engine, string text, int count, int max)
        {
            var result = engine.Search(new Query { Text = text, MaxNumberOfRecordsToReturn = max });
            foreach (var r in result.Records)
                if (r.Score > 0 && r.DocumentKey >= 0 && r.DocumentKey < count) yield return (int)r.DocumentKey;
        }
    }

    /// <summary>
    /// A Combobox's search with Indx (its Search parameter): labels that start with what was typed,
    /// then labels that hold it, then what the micro index finds, so "osl" finds Europe/Oslo and
    /// "pokdex_number" finds pokedex_number. The index is built on the first search and again when
    /// the options change; a page holds one per list and disposes it with itself.
    /// </summary>
    public sealed class OptionSearch : IDisposable
    {
        private SearchEngine? _engine;
        private string[] _values = [];

        public const int MaxResults = 50;

        public IEnumerable<SelectField.SelectOption> Find(IReadOnlyList<SelectField.SelectOption> options, string text)
        {
            var needle = text.Trim();
            if (needle.Length == 0 || options.Count == 0) return options;
            if (!_values.SequenceEqual(options.Select(o => o.Value)))
            {
                _engine?.Dispose();
                _engine = null;
                _values = options.Select(o => o.Value).ToArray();
            }
            var order = new List<int>();
            var seen = new HashSet<int>();
            void Take(int i) { if (seen.Add(i)) order.Add(i); }
            for (int i = 0; i < options.Count; i++)
                if (options[i].Label.StartsWith(needle, StringComparison.OrdinalIgnoreCase)) Take(i);
            for (int i = 0; i < options.Count; i++)
                if (options[i].Label.Contains(needle, StringComparison.OrdinalIgnoreCase)) Take(i);
            try
            {
                // Underscores and slashes part the words, so "sp attack" and "oslo" find their option.
                _engine ??= MicroIndex.Build(options.Select(o => o.Label.Replace('_', ' ').Replace('/', ' ')));
                foreach (var i in MicroIndex.Find(_engine, needle, options.Count, MaxResults)) Take(i);
            }
            catch
            {
                // The fuzzy match is a convenience: the label matches above still stand.
            }
            return order.Take(MaxResults).Select(i => options[i]);
        }

        public void Dispose()
        {
            _engine?.Dispose();
            _engine = null;
        }
    }
}
