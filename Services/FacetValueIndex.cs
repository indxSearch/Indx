using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Indx.Api;
using IndxServer.Components.Shared;
using IndxServer.Engine;

namespace IndxServer.Services
{
    /// <summary>
    /// The values of a facetable field as an index of their own, to search them as a visitor types
    /// into a long facet ("Search actors…"): a micro-index per field whose records are the field's
    /// distinct values, matched with Indx's own matching, typos, word starts and whole words anywhere
    /// in a value included (14 Nov 2026). Prototyped on tmdb23k's 89 367 actors: 73 ms to read the
    /// values, a fraction of a second to index them, a search in 0.1 to 1.5 ms; "tom hnks" finds Tom
    /// Hanks and "scarlet johanson" Scarlett Johansson, where the regex others offer finds neither.
    /// <para>Built on the first search of a field (or when its panel field gets the focus) and kept
    /// in step with the data without a rebuild per change (15 Nov 2026): the engine answers its empty
    /// search from a cache that every insert, update and delete clears, so a new facet answer means
    /// the data changed. The values it holds that the index does not are then inserted into the
    /// micro-index, a few records in milliseconds; values gone stay (their count is 0, so they are
    /// never shown) until they are a fifth of the index, which is then rebuilt off the request. Until
    /// then a rebuild came on every change of the record count, paid by a visitor's keystroke (0.7 s
    /// for 89 367 actors), and a value an update added was missing for up to ten minutes. A new
    /// engine (a reindex, a swap) builds afresh. The counts are not kept here: the route counts the
    /// facet over the visitor's search, or over every record.</para>
    /// </summary>
    public sealed class FacetValueIndex(ILogger<FacetValueIndex> logger) : IDisposable
    {
        /// <summary>The values a search of the micro-index takes before they are ordered by count:
        /// enough that a common value reached by a short text ("jackson", 146 actors) is among them.</summary>
        public const int Matches = 500;

        /// <summary>The share of an index's values gone from the data at which it is rebuilt.</summary>
        private const double RebuildAtGone = 0.2;

        /// <summary>A micro-index nobody has searched for this long is let go. It costs memory while
        /// held (about 110 MB for 89 367 actors, 18 MB for 9 910 directors, measured in-process; words
        /// and matching inside them, as any searched field), so it is held only while a facet is in use.</summary>
        public static readonly TimeSpan IdleAfter = TimeSpan.FromMinutes(30);

        private sealed class Entry(string fingerprint, SearchEngine engine, string[] values, DateTime built) : IDisposable
        {
            public string Fingerprint { get; } = fingerprint;
            public SearchEngine Engine { get; } = engine;
            /// <summary>The value of each record of the micro-index, by its key. Replaced whole, never
            /// changed in place, so a search reading it meanwhile sees one or the other.</summary>
            public volatile string[] Values = values;
            /// <summary>The engine's facet answer the index was last brought in step with.</summary>
            public object? Facet;
            public HashSet<string> Held { get; } = new(values, StringComparer.Ordinal);
            public bool Rebuilding;
            public object Sync { get; } = new();
            public DateTime Built { get; } = built;
            public DateTime LastUsed { get; set; } = built;
            public void Dispose() => Engine.Dispose();
        }

        private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, object> _building = new(StringComparer.Ordinal);

        /// <summary>The values of <paramref name="field"/> that <paramref name="text"/> finds, best
        /// match first: the rung of fewest edits that has any, so "tom hanks" gives Tom Hanks and not
        /// every Tom within two edits. When no value holds every word, the fallback, closest first
        /// (<c>Fallback</c> true): the values holding the most of the words, then the pattern matches.
        /// So Donna Reed stays in the list while "donna rea" is typed, and "leodicaprio" (the start of a
        /// word run into the next) finds Leonardo DiCaprio through the pattern index.
        /// <paramref name="readFacet"/> gives every value of the field when the micro-index has to be
        /// built.</summary>
        public (IReadOnlyList<string> Values, bool Fallback) Find(string teamId, string dataSetName, IServerSearchEngine engine, string field, string text,
            Func<KeyValuePair<string, int>[]> readFacet)
        {
            var entry = Get(teamId, dataSetName, engine, field, readFacet);
            entry.LastUsed = DateTime.UtcNow;
            LetIdleGo();
            return Search(entry, text);
        }

        /// <summary>Builds the field's micro-index if it is not built, so the first letter typed does
        /// not wait for it: a panel asks when its search field gets the focus.</summary>
        public void Warm(string teamId, string dataSetName, IServerSearchEngine engine, string field, Func<KeyValuePair<string, int>[]> readFacet) =>
            Get(teamId, dataSetName, engine, field, readFacet).LastUsed = DateTime.UtcNow;

        /// <summary>The most values a visitor's search may hold for <see cref="FindAmong"/>.</summary>
        public const int AmongLimit = 3000;

        /// <summary>A search's own values, indexed apart, at most this many kept.</summary>
        private const int AmongKept = 32;

        private readonly ConcurrentDictionary<string, Entry> _among = new(StringComparer.Ordinal);

        /// <summary>
        /// As <see cref="Find"/>, among the values a visitor's search holds (at most
        /// <see cref="AmongLimit"/>): "le" within the search "titanic" (14 Nov 2026). Over the whole
        /// field "le" starts thousands of names, the 500 taken missed Titanic's cast, and nothing was
        /// left once the values the search does not hold were dropped. The values are indexed apart,
        /// a few milliseconds for a few hundred, and kept for the next keystrokes of the same search.
        /// </summary>
        public (IReadOnlyList<string> Values, bool Fallback) FindAmong(string teamId, string dataSetName, string field,
            IReadOnlyCollection<string> values, string text)
        {
            var sorted = values.Order(StringComparer.Ordinal).ToArray();
            var hash = new HashCode();
            foreach (var v in sorted) hash.Add(v, StringComparer.Ordinal);
            var key = $"{teamId}\0{dataSetName}\0{field}\0{sorted.Length}\0{hash.ToHashCode()}";
            if (!_among.TryGetValue(key, out var entry))
            {
                entry = new Entry(key, MicroIndex.Build(sorted, IndxServerInternalApi.GetLicensePath()), sorted, DateTime.UtcNow);
                if (!_among.TryAdd(key, entry)) { Retire(entry); entry = _among[key]; }
                // The oldest let go: a search's values matter while its visitor types.
                foreach (var (oldKey, old) in _among.OrderBy(e => e.Value.LastUsed).Take(Math.Max(0, _among.Count - AmongKept)).ToList())
                    if (_among.TryRemove(new KeyValuePair<string, Entry>(oldKey, old))) Retire(old);
            }
            entry.LastUsed = DateTime.UtcNow;
            return Search(entry, text);
        }

        private static (IReadOnlyList<string> Values, bool Fallback) Search(Entry entry, string text)
        {
            if (entry.Values.Length == 0) return ([], false);
            var query = new Query { Text = text, MaxNumberOfRecordsToReturn = Matches };
            var result = entry.Engine.Search(query);
            // Read after the search: a value inserted meanwhile is in the array before its record.
            var values = entry.Values;
            var found = new List<string>(result.Records?.Length ?? 0);
            foreach (var record in result.Records ?? [])
                if (record.DocumentKey >= 0 && record.DocumentKey < values.Length)
                    found.Add(values[record.DocumentKey]);
            return (found, result.IsFallback);
        }

        private static string[] Present(KeyValuePair<string, int>[] facet) =>
            facet.Where(v => v.Value > 0 && v.Key is not ("null" or "")).Select(v => v.Key).ToArray();

        private Entry Get(string teamId, string dataSetName, IServerSearchEngine engine, string field, Func<KeyValuePair<string, int>[]> readFacet)
        {
            var key = $"{teamId}\0{dataSetName}\0{field}";
            // The engine itself: a reindex or a swap is a new one, and a new index.
            var fingerprint = RuntimeHelpers.GetHashCode(engine).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var facet = readFacet();
            if (_entries.TryGetValue(key, out var known) && known.Fingerprint == fingerprint)
            {
                if (!ReferenceEquals(known.Facet, facet)) CatchUp(key, known, facet, dataSetName, field);
                return known;
            }
            // One build per field at a time; a search meanwhile waits for it rather than building again.
            lock (_building.GetOrAdd(key, _ => new object()))
            {
                if (_entries.TryGetValue(key, out known) && known.Fingerprint == fingerprint) return known;
                var built = Build(fingerprint, facet, dataSetName, field);
                if (_entries.TryGetValue(key, out var old)) Retire(old);
                _entries[key] = built;
                return built;
            }
        }

        private Entry Build(string fingerprint, KeyValuePair<string, int>[] facet, string dataSetName, string field)
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var values = Present(facet);
            var built = new Entry(fingerprint, MicroIndex.Build(values, IndxServerInternalApi.GetLicensePath()), values, DateTime.UtcNow) { Facet = facet };
            logger.LogInformation("Facet values of {Field} in {DataSet}: {Count} values indexed in {Ms} ms",
                field, dataSetName, values.Length, (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return built;
        }

        /// <summary>Brings an index in step with a new facet answer: the values new to it inserted,
        /// and a rebuild off the request when a fifth of what it holds is gone from the data.</summary>
        private void CatchUp(string key, Entry entry, KeyValuePair<string, int>[] facet, string dataSetName, string field)
        {
            lock (entry.Sync)
            {
                if (ReferenceEquals(entry.Facet, facet)) return;
                var present = Present(facet);
                var added = present.Where(v => !entry.Held.Contains(v)).ToArray();
                if (added.Length > 0)
                {
                    var start = entry.Values.Length;
                    // The values first, so a search that meets a new record finds its value.
                    entry.Values = [.. entry.Values, .. added];
                    var records = added.Select((v, i) => System.Text.Json.JsonSerializer.Serialize(new { key = start + i, text = v }));
                    if (!entry.Engine.InsertJsonRecords(records, null, out var error))
                        logger.LogWarning("Facet values of {Field} in {DataSet}: {Count} new values not inserted: {Error}", field, dataSetName, added.Length, error);
                    entry.Held.UnionWith(added);
                }
                entry.Facet = facet;
                var gone = entry.Held.Count - present.Length;
                if (gone > RebuildAtGone * entry.Held.Count && !entry.Rebuilding)
                {
                    entry.Rebuilding = true;
                    _ = Task.Run(() =>
                    {
                        var rebuilt = Build(entry.Fingerprint, facet, dataSetName, field);
                        if (_entries.TryUpdate(key, rebuilt, entry)) Retire(entry);
                        else rebuilt.Dispose();
                    });
                }
            }
        }

        private void LetIdleGo()
        {
            var now = DateTime.UtcNow;
            foreach (var (key, entry) in _entries)
                if (now - entry.LastUsed > IdleAfter && _entries.TryRemove(new KeyValuePair<string, Entry>(key, entry)))
                    Retire(entry);
        }

        /// <summary>Forgets a dataset's micro-indexes (deleted, or emptied).</summary>
        public void Forget(string teamId, string dataSetName)
        {
            var prefix = $"{teamId}\0{dataSetName}\0";
            foreach (var key in _entries.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
                if (_entries.TryRemove(key, out var entry)) Retire(entry);
        }

        /// <summary>Disposes a replaced micro-index once the searches that may still hold it are
        /// done: it holds native memory, and a search takes milliseconds.</summary>
        private static void Retire(Entry entry) =>
            _ = Task.Delay(TimeSpan.FromSeconds(30)).ContinueWith(_ => entry.Dispose(), TaskScheduler.Default);

        public void Dispose()
        {
            foreach (var entry in _entries.Values.Concat(_among.Values)) entry.Dispose();
            _entries.Clear();
            _among.Clear();
        }
    }
}
