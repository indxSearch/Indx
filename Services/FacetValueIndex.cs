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
    /// <para>Built on the first search of a field and kept, rebuilt when the dataset's engine, its
    /// record count or its last index build changes, and at most <see cref="MaxAge"/> after it was
    /// built, so a value an update renamed comes in without a rebuild per change. The counts are not
    /// kept here: the route counts the facet over the visitor's search, or over every record.</para>
    /// </summary>
    public sealed class FacetValueIndex(ILogger<FacetValueIndex> logger) : IDisposable
    {
        /// <summary>The values a search of the micro-index takes before they are ordered by count:
        /// enough that a common value reached by a short text ("jackson", 146 actors) is among them.</summary>
        public const int Matches = 500;

        /// <summary>How long a micro-index serves before it is built again on its next search.</summary>
        public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(10);

        /// <summary>A micro-index nobody has searched for this long is let go. It costs memory while
        /// held (about 110 MB for 89 367 actors, 18 MB for 9 910 directors, measured in-process; words
        /// and matching inside them, as any searched field), so it is held only while a facet is in use.</summary>
        public static readonly TimeSpan IdleAfter = TimeSpan.FromMinutes(30);

        private sealed class Entry(string fingerprint, SearchEngine engine, string[] values, DateTime built) : IDisposable
        {
            public string Fingerprint { get; } = fingerprint;
            public SearchEngine Engine { get; } = engine;
            public string[] Values { get; } = values;
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
        /// <paramref name="readValues"/> gives every value of the field when the micro-index has to be
        /// built.</summary>
        public (IReadOnlyList<string> Values, bool Fallback) Find(string teamId, string dataSetName, IServerSearchEngine engine, string field, string text,
            Func<IReadOnlyList<string>> readValues)
        {
            var entry = Get(teamId, dataSetName, engine, field, readValues);
            entry.LastUsed = DateTime.UtcNow;
            LetIdleGo();
            return Search(entry, text);
        }

        /// <summary>Builds the field's micro-index if it is not built, so the first letter typed does
        /// not wait for it: a panel asks when its search field gets the focus.</summary>
        public void Warm(string teamId, string dataSetName, IServerSearchEngine engine, string field, Func<IReadOnlyList<string>> readValues) =>
            Get(teamId, dataSetName, engine, field, readValues).LastUsed = DateTime.UtcNow;

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
            var found = new List<string>(result.Records?.Length ?? 0);
            foreach (var record in result.Records ?? [])
                if (record.DocumentKey >= 0 && record.DocumentKey < entry.Values.Length)
                    found.Add(entry.Values[record.DocumentKey]);
            return (found, result.IsFallback);
        }

        private Entry Get(string teamId, string dataSetName, IServerSearchEngine engine, string field, Func<IReadOnlyList<string>> readValues)
        {
            var key = $"{teamId}\0{dataSetName}\0{field}";
            var status = engine.Status;
            var fingerprint = $"{RuntimeHelpers.GetHashCode(engine)}|{status.DocumentCount}|{status.TimeOfLastIndexBuild:O}";
            bool Fresh(Entry e) => e.Fingerprint == fingerprint && DateTime.UtcNow - e.Built < MaxAge;
            if (_entries.TryGetValue(key, out var known) && Fresh(known)) return known;
            // One build per field at a time; a search meanwhile waits for it rather than building again.
            lock (_building.GetOrAdd(key, _ => new object()))
            {
                if (_entries.TryGetValue(key, out known) && Fresh(known)) return known;
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                var values = readValues().ToArray();
                var built = new Entry(fingerprint, MicroIndex.Build(values, IndxServerInternalApi.GetLicensePath()), values, DateTime.UtcNow);
                if (_entries.TryGetValue(key, out var old)) Retire(old);
                _entries[key] = built;
                logger.LogInformation("Facet values of {Field} in {DataSet}: {Count} values indexed in {Ms} ms",
                    field, dataSetName, values.Length, (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                return built;
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
