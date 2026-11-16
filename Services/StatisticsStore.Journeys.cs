using Indx.Api;

namespace IndxServer.Services
{
    /// <summary>What a step of a journey was.</summary>
    public enum JourneyStepKind
    {
        /// <summary>A visit's first search, by its text.</summary>
        Search,
        /// <summary>A visit that began with no text and no pick: a catalogue page.</summary>
        Browse,
        /// <summary>A value ticked from its facet's own search field, the field and value its label.</summary>
        Pick,
        /// <summary>A later search with other text, whatever the text: its texts are as many as its visits.</summary>
        Again,
        /// <summary>Filters added, one after the other, as one step.</summary>
        Filter,
        /// <summary>Filters taken off.</summary>
        Unfilter,
        /// <summary>A result chosen from the search before it.</summary>
        Click,
        /// <summary>A conversion on a result of the search before it.</summary>
        Convert,
        /// <summary>The visit ended without choosing a result.</summary>
        Left,
        /// <summary>The rest of a long visit, past <see cref="StatisticsStore.JourneyDepth"/> steps.</summary>
        More,
        /// <summary>Ways on too thin to show, folded into one.</summary>
        Rest,
        /// <summary>The starts too rare to list, as one start.</summary>
        Tail,
    }

    /// <summary>
    /// One step of the journeys: what it was (<see cref="Kind"/>, <see cref="Label"/>: the text, the
    /// filter, the value picked), the visits that took it after the steps above it, how many of them
    /// went on to choose a result and to convert, the ways on, most taken first, and for a step that
    /// stands for many texts (a later search, a pick, the tail of starts) the most common of them.
    /// </summary>
    public sealed record JourneyNode(string Id, JourneyStepKind Kind, string Label, long Visits, long Clicked, long Converted,
        IReadOnlyList<JourneyNode> Children, IReadOnlyList<JourneyText>? Texts = null);

    /// <summary>A text a step stands for, with its visits; for a pick what was typed, and whether the
    /// value came from the fallback (<see cref="NearMatch"/>).</summary>
    public readonly record struct JourneyText(string Text, long Visits, bool NearMatch = false);

    /// <summary>The journeys of a window: the root's children are the starts. How many sessions were
    /// read, and whether they were a sample; the counted searches of the window, and how many of them
    /// carried a session, since only those can be followed.</summary>
    public sealed record JourneyForest(JourneyNode Root, long Sessions, bool Sampled, long Searches, long WithSession);

    public sealed partial class StatisticsStore
    {
        /// <summary>The steps a journey shows; past them the rest of the visit is one "More steps",
        /// then how it ended.</summary>
        public const int JourneyDepth = 6;
        /// <summary>A start with this many visits or fewer is folded into the tail's one start, when
        /// there are more starts than <see cref="JourneyListedStarts"/>.</summary>
        public const int JourneyTailVisits = 2;
        /// <summary>The starts a menu lists; a window with more folds its rare ones into one.</summary>
        public const int JourneyListedStarts = 200;
        /// <summary>The most sessions read; past it a sample of about this many.</summary>
        public const int JourneyMaxSessions = 50_000;
        /// <summary>The ways on from a step that are never folded, the most taken: what the canvas shows.</summary>
        public const int JourneyKeptWays = 5;
        private const double JourneyThinShare = 0.01;
        private const int JourneyThinVisits = 3;
        private const int JourneyTexts = 10;

        /// <summary>
        /// Journeys (16 Nov 2026, Notes/statistics-browsing-journeys-plan-2026-11-15.md): how the visits
        /// of a window went on from how they began, to a chosen result, a conversion or nothing. A visit
        /// is a session's counted, settled searches in order, so only clients that send <c>?session=</c>
        /// are in it (intrface sends one per page load); Browsing's paths need none.
        ///
        /// <para>Bounded so it holds on a busy dataset, where there are more distinct paths than
        /// visits: a later search is one step whatever its text; filters added one after the other
        /// are one step, in one order; a visit past <see cref="JourneyDepth"/> steps ends in "More
        /// steps" and its outcome; with more starts than <see cref="JourneyListedStarts"/>, those with
        /// <see cref="JourneyTailVisits"/> visits or fewer are one start (on a small dataset every
        /// start has a visit or two, and every one is listed); below the starts, ways on past the
        /// <see cref="JourneyKeptWays"/> most taken and under 1 % of their step's visits, or under 3,
        /// are folded into one (on a small dataset every way on is thin, and folding them all left a
        /// tree of one step). Past <see cref="JourneyMaxSessions"/> sessions a sample is read, every
        /// n-th session by its id, so the whole window is in it rather than its newest days.</para>
        ///
        /// <para>From the raw rows, so it reaches back as far as they are kept.</para>
        /// </summary>
        public JourneyForest Journeys(string teamId, string dataSet, long fromDay, long toDay, string? source = null)
        {
            var tz = ZoneOf(teamId, dataSet);
            using var conn = Open();
            void Bind(Microsoft.Data.Sqlite.SqliteCommand cmd)
            {
                AddSource(cmd, source);
                cmd.Parameters.AddWithValue("$t", teamId);
                cmd.Parameters.AddWithValue("$d", dataSet);
                cmd.Parameters.AddWithValue("$liveFrom", StatisticsDays.Start(tz, fromDay));
                cmd.Parameters.AddWithValue("$toEx", StatisticsDays.Start(tz, toDay + 1));
            }
            var where = LiveWhereFor(source) + " AND s.Counted = 1";

            long searches, withSession, sessions;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $@"SELECT COUNT(*), COUNT(s.Session), COUNT(DISTINCT s.Session) FROM SearchEvents s
                    WHERE {where} AND (s.Session IS NULL OR s.Superseded = 0)";
                Bind(cmd);
                using var r = cmd.ExecuteReader();
                r.Read();
                (searches, withSession, sessions) = (r.GetInt64(0), r.GetInt64(1), r.GetInt64(2));
            }

            // Every n-th session by the last two characters of its id, a random id: the sample spans
            // the window. About n, not exactly: the characters are not spread perfectly.
            long stride = sessions > JourneyMaxSessions ? (sessions + JourneyMaxSessions - 1) / JourneyMaxSessions : 1;
            var visits = new List<List<JourneyRow>>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $@"
SELECT s.Session, s.QueryText, s.FilterKey, s.PickField, s.PickValue, s.PickText, s.PickFallback,
       EXISTS (SELECT 1 FROM SelectEvents x WHERE x.QueryId = s.QueryId AND x.Counted = 1),
       EXISTS (SELECT 1 FROM ConvertEvents x WHERE x.QueryId = s.QueryId AND x.Counted = 1)
  FROM SearchEvents s
 WHERE {where} AND s.Session IS NOT NULL AND s.Superseded = 0
   {(stride > 1 ? "AND (unicode(substr(s.Session, -1)) * 31 + unicode(substr(s.Session, -2, 1))) % $stride = 0" : "")}
 ORDER BY s.Session, s.Timestamp, s.rowid";
                Bind(cmd);
                if (stride > 1) cmd.Parameters.AddWithValue("$stride", stride);
                using var r = cmd.ExecuteReader();
                string? session = null;
                List<JourneyRow>? visit = null;
                while (r.Read())
                {
                    var s = r.GetString(0);
                    if (s != session) { session = s; visits.Add(visit = []); }
                    visit!.Add(new JourneyRow(r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2),
                        r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4),
                        r.IsDBNull(5) ? null : r.GetString(5), !r.IsDBNull(6) && r.GetInt64(6) == 1,
                        r.GetInt64(7) == 1, r.GetInt64(8) == 1));
                }
            }
            return new JourneyForest(JourneyTree(visits.Select(JourneySteps).ToList()), visits.Count, stride > 1, searches, withSession);
        }

        /// <summary>One settled search of a visit, as Journeys reads it.</summary>
        public readonly record struct JourneyRow(string Text, string? FilterKey, string? PickField = null, string? PickValue = null,
            string? PickText = null, bool PickFallback = false, bool Clicked = false, bool Converted = false);

        /// <summary>A step of one visit, before the visits are merged.</summary>
        public readonly record struct JourneyStep(JourneyStepKind Kind, string Label, string? Text = null, bool NearMatch = false);

        /// <summary>A search's text as a start: as Top queries groups it, and with its spaces tidied.</summary>
        public static string JourneyText(string text) =>
            string.Join(' ', text.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        /// <summary>The filters a search narrowed by, each as a step reads it: "genres = drama" (a value
        /// as the key holds it), "year 1990–1999" (a range). A negated operand, a position or a custom
        /// filter is no choice of a value and is left out, as Browsing leaves it.</summary>
        private static HashSet<string> JourneyFilters(string? key)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(key)) return set;
            foreach (var o in FilterKeyReader.Operands(key))
            {
                if (o.Negated) continue;
                if (o.Kind is FilterOperandKind.Value or FilterOperandKind.CaseSensitiveValue) set.Add($"{o.Field} = {o.Value}");
                else if (o.Kind == FilterOperandKind.Range && o.Min != null && o.Max != null)
                    set.Add($"{o.Field} {RangeBands.Exact.Of(Math.Min(o.Min.Value, o.Max.Value), Math.Max(o.Min.Value, o.Max.Value))}");
            }
            return set;
        }

        /// <summary>One visit's searches as steps: how it began, then for each search what changed
        /// (other text, filters added, a value picked, filters taken off) and a result chosen or
        /// converted on; "Left" when nothing was chosen. Past <see cref="JourneyDepth"/> steps, "More
        /// steps" and how it ended.</summary>
        public static List<JourneyStep> JourneySteps(IReadOnlyList<JourneyRow> rows)
        {
            var steps = new List<JourneyStep>();
            var had = new HashSet<string>(StringComparer.Ordinal);
            string? text = null;
            bool clicked = false;
            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                var t = JourneyText(row.Text);
                var filters = JourneyFilters(row.FilterKey);
                // The value picked, as the key holds it: a case-insensitive filter's value is lowercased there.
                string? picked = row.PickField == null || row.PickValue == null ? null
                    : filters.FirstOrDefault(f => string.Equals(f, $"{row.PickField} = {row.PickValue}", StringComparison.OrdinalIgnoreCase));
                var pick = picked == null ? (JourneyStep?)null
                    : new JourneyStep(JourneyStepKind.Pick, $"{row.PickField} = {row.PickValue}", row.PickText, row.PickFallback);

                if (i == 0)
                {
                    if (t.Length > 0) steps.Add(new JourneyStep(JourneyStepKind.Search, t));
                    else if (pick is { } first) { steps.Add(first); had.Add(picked!); }
                    else steps.Add(new JourneyStep(JourneyStepKind.Browse, "Browsed, no search"));
                }
                else if (t != text)
                    steps.Add(new JourneyStep(JourneyStepKind.Again, "Searched again", t.Length > 0 ? t : "(no text)"));

                var added = filters.Where(f => !had.Contains(f)).ToList();
                if (picked != null && added.Remove(picked)) steps.Add(pick!.Value);
                if (added.Count > 0)
                {
                    // Ticks one after the other are one step, in one order: the order of two ticks is no intent.
                    if (steps[^1].Kind == JourneyStepKind.Filter)
                        steps[^1] = steps[^1] with { Label = string.Join(" + ", steps[^1].Label.Split(" + ").Concat(added).Distinct().Order(StringComparer.Ordinal)) };
                    else steps.Add(new JourneyStep(JourneyStepKind.Filter, string.Join(" + ", added.Order(StringComparer.Ordinal))));
                }
                var removed = had.Where(f => !filters.Contains(f)).Order(StringComparer.Ordinal).ToList();
                if (removed.Count > 0) steps.Add(new JourneyStep(JourneyStepKind.Unfilter, string.Join(" + ", removed)));
                had = filters;
                text = t;
                if (row.Clicked) steps.Add(new JourneyStep(JourneyStepKind.Click, "Chose a result"));
                if (row.Converted) steps.Add(new JourneyStep(JourneyStepKind.Convert, "Converted"));
                clicked |= row.Clicked;
            }
            if (!clicked) steps.Add(new JourneyStep(JourneyStepKind.Left, "Left without a click"));
            if (steps.Count <= JourneyDepth) return steps;

            var tail = steps.Skip(JourneyDepth - 1).ToList();
            var ends = new List<JourneyStep>();
            if (tail.FirstOrDefault(s => s.Kind == JourneyStepKind.Click) is { Label: not null } click) ends.Add(click);
            if (tail.FirstOrDefault(s => s.Kind == JourneyStepKind.Convert) is { Label: not null } convert) ends.Add(convert);
            if (tail[^1].Kind == JourneyStepKind.Left) ends.Add(tail[^1]);
            return [.. steps.Take(JourneyDepth - 1), new JourneyStep(JourneyStepKind.More, "More steps"), .. ends];
        }

        private sealed class JourneyBuild(string id, JourneyStep step)
        {
            public readonly string Id = id;
            public readonly JourneyStep Step = step;
            public long Visits, Clicked, Converted;
            public readonly List<JourneyBuild> Children = [];
            public readonly Dictionary<string, JourneyBuild> Index = new(StringComparer.Ordinal);
            public Dictionary<(string Text, bool Near), long>? Texts;
        }

        /// <summary>The visits' steps merged into one tree, one node per step after the same steps
        /// before it, with the tail of starts and the thin ways on folded.</summary>
        public static JourneyNode JourneyTree(IReadOnlyList<List<JourneyStep>> visits)
        {
            static string KeyOf(JourneyStep s) => s.Kind is JourneyStepKind.Click or JourneyStepKind.Convert or JourneyStepKind.Left or JourneyStepKind.More
                ? s.Kind.ToString() : $"{s.Kind}:{s.Label}";

            var startVisits = visits.Where(v => v.Count > 0).GroupBy(v => KeyOf(v[0]), StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            // Only past what a menu lists: on a small dataset every start is rare, and all are listed.
            int rare = startVisits.Count > JourneyListedStarts ? startVisits.Values.Count(n => n <= JourneyTailVisits) : 0;
            var tailStart = new JourneyStep(JourneyStepKind.Tail, $"{rare:#,##0} {(rare == 1 ? "start" : "starts")} seen once or twice");

            var root = new JourneyBuild("j", new JourneyStep(JourneyStepKind.Browse, "Every visit"));
            foreach (var steps in visits)
            {
                if (steps.Count == 0) continue;
                bool clicked = steps.Any(s => s.Kind == JourneyStepKind.Click), converted = steps.Any(s => s.Kind == JourneyStepKind.Convert);
                var node = root;
                Count(node);
                for (int i = 0; i < steps.Count; i++)
                {
                    var step = steps[i];
                    if (i == 0 && rare > 0 && startVisits[KeyOf(step)] <= JourneyTailVisits)
                        step = tailStart with { Text = step.Kind == JourneyStepKind.Search ? step.Label : step.Kind == JourneyStepKind.Pick ? $"picked {step.Label}" : step.Label };
                    var key = KeyOf(step);
                    if (!node.Index.TryGetValue(key, out var child))
                    {
                        child = new JourneyBuild($"{node.Id}/{key}", step with { Text = null, NearMatch = false });
                        node.Index[key] = child;
                        node.Children.Add(child);
                    }
                    Count(child);
                    if (step.Text != null)
                    {
                        child.Texts ??= [];
                        child.Texts[(step.Text, step.NearMatch)] = child.Texts.GetValueOrDefault((step.Text, step.NearMatch)) + 1;
                    }
                    node = child;
                }

                void Count(JourneyBuild n)
                {
                    n.Visits++;
                    if (clicked) n.Clicked++;
                    if (converted) n.Converted++;
                }
            }

            JourneyNode Freeze(JourneyBuild b, int depth)
            {
                var kids = b.Children;
                // Below the starts only: the menu lists every start.
                if (depth >= 1)
                {
                    var floor = Math.Max(JourneyThinVisits, b.Visits * JourneyThinShare);
                    var thin = kids.OrderByDescending(c => c.Visits).ThenBy(c => c.Id, StringComparer.Ordinal)
                        .Skip(JourneyKeptWays).Where(c => c.Visits < floor).ToList();
                    if (thin.Count > 1)
                    {
                        var rest = new JourneyBuild($"{b.Id}/Rest", new JourneyStep(JourneyStepKind.Rest, $"{thin.Count} other steps"));
                        foreach (var c in thin) { rest.Visits += c.Visits; rest.Clicked += c.Clicked; rest.Converted += c.Converted; }
                        kids = [.. kids.Except(thin), rest];
                    }
                }
                var texts = b.Texts?.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key.Text, StringComparer.Ordinal).Take(JourneyTexts)
                    .Select(kv => new JourneyText(kv.Key.Text, kv.Value, kv.Key.Near)).ToList();
                return new JourneyNode(b.Id, b.Step.Kind, b.Step.Label, b.Visits, b.Clicked, b.Converted,
                    kids.OrderByDescending(c => c.Visits).ThenBy(c => c.Id, StringComparer.Ordinal).Select(c => Freeze(c, depth + 1)).ToList(), texts);
            }
            return Freeze(root, 0);
        }
    }
}
