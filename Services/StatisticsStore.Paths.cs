namespace IndxServer.Services
{
    /// <summary>
    /// One step of the browsing paths: the fields it added to its parent's (<see cref="Step"/>,
    /// several when they were set at once, a field's value when only one was used), the searches
    /// that came this way (<see cref="Searches"/>, itself and every path below it), the searches
    /// that stopped here (<see cref="Here"/>), the ways on from it, most taken first, the values of
    /// its fields as used by everyone who came this way, most used first (<see cref="Values"/>,
    /// the summary a person reads), and how many of the searches found nothing (<see cref="Empty"/>).
    /// </summary>
    public sealed record PathNode(string Step, long Searches, long Here, IReadOnlyList<PathNode> Children, string? Key = null,
        IReadOnlyList<PathValue>? Values = null, long Empty = 0)
    {
        // Key: the filter people used most at exactly this step, as stored, for the console to run
        // and list what it finds; null for a fold ("Other", "…") and the root.

        /// <summary>The share of the searches that came this way and went on to narrow further.</summary>
        public double Deeper => Searches == 0 ? 0 : (double)(Searches - Here) / Searches;
    }

    /// <summary>A value of a step's field and how many searches that came this way used it:
    /// "type1 = water", "speed 50–100".</summary>
    public readonly record struct PathValue(string Label, long Count);

    /// <summary>One set of fields as the window searched it: its fields, how many searches, the
    /// stored filter most of them used, how many found nothing, and each field's values with their
    /// searches.</summary>
    public sealed record PathSet(string[] Steps, long Count, string? Key = null, long Empty = 0,
        IReadOnlyDictionary<string, Dictionary<string, long>>? Values = null);

    /// <summary>A field's range bands: <see cref="Count"/> even bands of <see cref="Width"/> from
    /// <see cref="Start"/>, on round numbers.</summary>
    public readonly record struct RangeBands(double Start, double Width, int Count)
    {
        /// <summary>No bands: the field was only ever searched with one range, which is then the step.</summary>
        public static readonly RangeBands Exact = new(0, 0, 0);

        /// <summary>The band a range falls in, by its middle, as "a–b"; the range itself where there are no bands.</summary>
        public string Of(double min, double max)
        {
            if (Count == 0) return $"{N(min)}–{N(max)}";
            var i = Math.Clamp((int)Math.Floor(((min + max) / 2 - Start) / Width), 0, Count - 1);
            return $"{N(Start + i * Width)}–{N(Start + (i + 1) * Width)}";
        }

        private static string N(double v) => v.ToString(Math.Abs(v % 1) < 1e-9 ? "0" : "0.##", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>Four bands over the span the ranges cover, their width a round number (1, 2 or 5
        /// times a power of ten), the start a multiple of it.</summary>
        public static RangeBands Over(double low, double high, int count = 4)
        {
            var span = Math.Max(high - low, 1e-9);
            var raw = span / count;
            var p = Math.Pow(10, Math.Floor(Math.Log10(raw)));
            var width = new[] { 1, 2, 5, 10 }.Select(m => m * p).First(w => w >= raw);
            var start = Math.Floor(low / width) * width;
            var n = (int)Math.Ceiling((high - start) / width - 1e-9);
            return new RangeBands(start, width, Math.Max(1, n));
        }
    }

    public sealed partial class StatisticsStore
    {
        /// <summary>Ways on from one step that are shown; the rest are folded into "Other".</summary>
        public const int PathBranches = 8;
        public const int PathDepth = 4;

        /// <summary>
        /// Browsing paths (28 Oct 2026): how visitors narrow a catalogue with its filters, read from
        /// the filters of the window's searches as a tree by containment, with no session and no
        /// visitor: a search on the fields {A, B} is taken to have come by {A}, the most searched of
        /// the sets one field shorter. That works for every client, intrface or not, since the filter
        /// of each search is recorded on the server (the Browsing report). It cannot tell the order
        /// filters were set in, only which sets were searched; on a catalogue the broader one nearly
        /// always came first.
        ///
        /// <para>A step is a field, not a value, so the tree is a summary a person reads: what people
        /// filter by and in what order, eight ways on at most. A step reads "type1 = water" when one
        /// value was used, "type1 (13 values)" when more were; the values, with their searches, are
        /// the step's <see cref="PathNode.Values"/>, for the panel. Several values in one search
        /// read "type1 (2 values)", more of one kind rather than deeper. A range falls in one of four
        /// bands over the span the window's ranges of that field cover, on round numbers ("price
        /// 0–50"), by its middle; a field searched with one range only keeps it. Negated operands are
        /// left out, as the Browsing report leaves them.</para>
        ///
        /// <para>From the raw rows, so it reaches back as far as they are kept
        /// (<see cref="RetentionDays"/>), and counted as the Browsing report counts them
        /// (<c>CountedSearches</c>: a search's quick and its facet call count once).</para>
        /// </summary>
        public PathNode BrowsingPaths(string teamId, string dataSet, long fromDay, long toDay, string? source = null)
        {
            var tz = ZoneOf(teamId, dataSet);
            var keys = new Dictionary<string, (long Count, long Empty)>(StringComparer.Ordinal);
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT c.FilterKey, c.HitCount FROM " + CountedSearches(LiveWhereFor(source)) + " c WHERE c.FilterKey IS NOT NULL AND c.FilterKey <> ''";
                AddSource(cmd, source);
                cmd.Parameters.AddWithValue("$t", teamId);
                cmd.Parameters.AddWithValue("$d", dataSet);
                cmd.Parameters.AddWithValue("$liveFrom", StatisticsDays.Start(tz, fromDay));
                cmd.Parameters.AddWithValue("$toEx", StatisticsDays.Start(tz, toDay + 1));
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var k = r.GetString(0);
                    var had = keys.GetValueOrDefault(k);
                    keys[k] = (had.Count + 1, had.Empty + (r.GetInt32(1) == 0 ? 1 : 0));
                }
            }
            return Tree(Sets(keys));
        }

        public static Dictionary<string, PathSet> Sets(IReadOnlyDictionary<string, long> keys) =>
            Sets(keys.ToDictionary(kv => kv.Key, kv => (kv.Value, 0L), StringComparer.Ordinal));

        /// <summary>The window's distinct filters by their set of fields, counted, with each field's
        /// values (range bands for a range): the input of <see cref="Tree"/>.</summary>
        public static Dictionary<string, PathSet> Sets(IReadOnlyDictionary<string, (long Count, long Empty)> keys)
        {
            var spans = new Dictionary<string, (double Low, double High, HashSet<(double, double)> Seen)>(StringComparer.Ordinal);
            foreach (var key in keys.Keys)
                foreach (var r in FilterKeyOperands.ParseRanges(key))
                {
                    var sp = spans.TryGetValue(r.Field, out var had) ? (Math.Min(had.Low, r.Min), Math.Max(had.High, r.Max), had.Seen) : (r.Min, r.Max, new HashSet<(double, double)>());
                    sp.Item3.Add((r.Min, r.Max));
                    spans[r.Field] = sp;
                }
            // One range ever used is the step as it is; more are banded.
            var bands = spans.ToDictionary(kv => kv.Key,
                kv => kv.Value.Seen.Count == 1 ? RangeBands.Exact : RangeBands.Over(kv.Value.Low, kv.Value.High), StringComparer.Ordinal);

            var sets = new Dictionary<string, (string[] Fields, long Count, long Empty, string Key, long KeyCount, Dictionary<string, Dictionary<string, long>> Values)>(StringComparer.Ordinal);
            foreach (var (key, (n, empty)) in keys)
            {
                var byField = FieldValues(key, bands);
                if (byField.Count == 0) continue;
                var fields = byField.Keys.OrderBy(f => f, StringComparer.Ordinal).ToArray();
                var id = string.Join('\n', fields);
                if (!sets.TryGetValue(id, out var set)) set = (fields, 0, 0, key, 0, new(StringComparer.Ordinal));
                foreach (var (field, label) in byField)
                {
                    var values = set.Values.TryGetValue(field, out var v) ? v : set.Values[field] = new(StringComparer.Ordinal);
                    values[label] = values.GetValueOrDefault(label) + n;
                }
                sets[id] = (fields, set.Count + n, set.Empty + empty, n > set.KeyCount ? key : set.Key, Math.Max(n, set.KeyCount), set.Values);
            }
            return sets.ToDictionary(kv => kv.Key,
                kv => new PathSet(kv.Value.Fields, kv.Value.Count, kv.Value.Key, kv.Value.Empty, kv.Value.Values), StringComparer.Ordinal);
        }

        /// <summary>A filter key's fields, each with the value it was searched on: "type1 = water",
        /// "type1 (2 values)" for several in one search, a range in its field's band when the bands
        /// are known, else "speed (range)".</summary>
        public static Dictionary<string, string> FieldValues(string key, IReadOnlyDictionary<string, RangeBands>? bands = null)
        {
            var ranges = FilterKeyOperands.ParseRanges(key).ToList();
            return FilterKeyOperands.Parse(key).Distinct()
                .GroupBy(o => o.Field, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g =>
                {
                    if (g.Any(o => o.Value.Length == 0))
                    {
                        var mine = ranges.Where(r => r.Field == g.Key).ToList();
                        return bands != null && mine.Count == 1 && bands.TryGetValue(g.Key, out var b)
                            ? $"{g.Key} {b.Of(mine[0].Min, mine[0].Max)}"
                            : $"{g.Key} (range)";
                    }
                    var values = g.Select(o => o.Value).Distinct(StringComparer.Ordinal).ToList();
                    return values.Count == 1 ? $"{g.Key} = {values[0]}" : $"{g.Key} ({values.Count} values)";
                }, StringComparer.Ordinal);
        }

        /// <summary>A filter key's steps as values, sorted: what <see cref="FieldValues"/> reads.</summary>
        public static string[] Steps(string key, IReadOnlyDictionary<string, RangeBands>? bands = null) =>
            FieldValues(key, bands).Values.OrderBy(s => s, StringComparer.Ordinal).ToArray();

        /// <summary>The tree by containment: each set under the most searched set one field
        /// shorter, or under the root when none was searched.</summary>
        public static PathNode Tree(IReadOnlyDictionary<string, PathSet> sets)
        {
            var children = new Dictionary<string, List<string>> { [""] = [] };
            foreach (var (id, set) in sets)
            {
                var steps = set.Steps;
                string parent = "";
                long best = -1;
                for (int i = 0; i < steps.Length; i++)
                {
                    var shorter = string.Join('\n', steps.Where((_, j) => j != i));
                    if (sets.TryGetValue(shorter, out var s) && s.Count > best) { best = s.Count; parent = shorter; }
                }
                (children.TryGetValue(parent, out var list) ? list : children[parent] = []).Add(id);
            }

            IEnumerable<string> Subtree(string id) => (id.Length == 0 ? [] : new[] { id }).Concat(children.GetValueOrDefault(id, []).SelectMany(Subtree));
            long Total(string id) => Subtree(id).Sum(x => sets[x].Count);

            PathNode Build(string id, string[] parentFields, int depth)
            {
                var here = id.Length == 0 ? 0 : sets[id].Count;
                var fields = id.Length == 0 ? [] : sets[id].Steps;
                var below = Subtree(id).ToList();
                var kids = children.GetValueOrDefault(id, []).Select(k => (Id: k, Total: Total(k))).OrderByDescending(k => k.Total).ThenBy(k => k.Id, StringComparer.Ordinal).ToList();
                // Past the branches the ways on are one "Other", past the depth one "…": they went
                // deeper all the same, and the widths still add up.
                var deep = depth >= PathDepth;
                var shown = deep ? [] : kids.Take(PathBranches).Select(k => Build(k.Id, fields, depth + 1)).ToList();
                var rest = (deep ? kids : kids.Skip(PathBranches)).Sum(k => k.Total);
                if (rest > 0) shown.Add(new PathNode(deep ? "…" : "Other", rest, rest, []));

                // The added fields' values, counted over everyone who came this way: the summary.
                var added = fields.Except(parentFields, StringComparer.Ordinal).ToList();
                var values = added
                    .SelectMany(f => below.SelectMany(x => sets[x].Values?.GetValueOrDefault(f) ?? []))
                    .GroupBy(v => v.Key, StringComparer.Ordinal)
                    .Select(g => new PathValue(g.Key, g.Sum(v => v.Value)))
                    .OrderByDescending(v => v.Count).ThenBy(v => v.Label, StringComparer.Ordinal)
                    .ToList();
                string StepOf(string field)
                {
                    var mine = below.SelectMany(x => sets[x].Values?.GetValueOrDefault(field)?.Keys ?? Enumerable.Empty<string>()).Distinct(StringComparer.Ordinal).ToList();
                    if (mine.Count == 1) return mine[0];
                    bool range = mine.Any(m => !m.StartsWith(field + " = ", StringComparison.Ordinal) && !m.EndsWith(" values)", StringComparison.Ordinal));
                    return $"{field} ({mine.Count} {(range ? "ranges" : "values")})";
                }
                return new PathNode(string.Join(" & ", added.Select(StepOf)), here + kids.Sum(k => k.Total), here, shown,
                    id.Length == 0 ? null : sets[id].Key, values, below.Sum(x => sets[x].Empty));
            }

            return Build("", [], 0);
        }
    }
}
