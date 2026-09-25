using System.Globalization;
using Indx.Api;

namespace IndxServer.Services
{
    /// <summary>
    /// Whether a field configuration change can be applied to the live engine, or has to go
    /// through a rebuild on a shadow engine.
    ///
    /// This class used to answer that itself, because the library did not. It carried two
    /// predicates of its own — one for a field getting its first role, whose documents hold no
    /// positions for it, and one for any Facetable change, because DocumentFields cached its
    /// facetable field list on first use and cleared it only in Dispose. Both were found on
    /// Millum, where two fields were made Facetable after a wake and never produced a facet.
    ///
    /// The library answers for both now, so both are gone:
    ///
    /// - <see cref="DocumentFields.RequiresReindex"/> reports a first role, and Index() rebuilds
    ///   the positions for a field that came into use since Load.
    /// - SetFieldConfiguration invalidates the cached field lists and the empty-search cache, so a
    ///   Facetable flag set on a live engine is seen at once — for a text search and for the empty
    ///   faceted search a facet panel makes, which were two separate caches and two separate fixes.
    /// - <see cref="DocumentFields.RequiresReload"/> reports the six flags only a Load can consume.
    ///   That one is in here for a harder reason than the others: applied in place, such a change
    ///   returns the live engine to Created, which would leave the dataset not Ready with no way
    ///   back until the next reload. The shadow applies it between Init and Load on a fresh engine,
    ///   where it is simply correct.
    ///
    /// What is left is the question, asked of the library, and the one thing the library still
    /// leaves to the caller: Filterable, Facetable and Sortable are plain properties on Field, so
    /// only a Searchable or Sortable change raises the notification that persists the
    /// configuration. <see cref="ApplyInPlace"/> saves it explicitly.
    ///
    /// Dropping the Facetable rebuild is the one with a number on it: RunFieldConfigurationOnShadow
    /// is synchronous and all three callers block on it, so ticking one Facetable box on a dataset
    /// the size of Millum was a ~16-second request, linear in document count, with a second full
    /// copy of the store and index resident while it ran. It is a flag write now.
    /// </summary>
    public static class FieldConfigurationChange
    {
        /// <summary>
        /// Tells the monitor what this change does, in words. A field configuration change is the
        /// one thing worth noticing while something else is driving the server: it is not a state
        /// the engine keeps afterwards, so polling cannot find it, and its effect on every search
        /// is immediate.
        ///
        /// <para>A single change is spelled out. Several are grouped, because an agent setting a
        /// dataset up changes eight fields at once and eight lines is not eight things worth
        /// reading.</para>
        /// </summary>
        public static void Announce(DocumentFields? current, FieldProxy[] proposed,
                                    string dataSetName, string teamId)
        {
            if (current == null) return;
            var text = Describe(current.GetField, proposed);
            if (text != null)
                Monitor.MonitorActivity.Report(dataSetName, teamId, text);
        }

        /// <summary>
        /// The sentence <see cref="Announce"/> reports, or null when nothing changed. Takes a
        /// lookup rather than the <see cref="DocumentFields"/> itself: looking a field up is all
        /// this needs, and a dictionary is something a test can build — populating a real
        /// DocumentFields is internal to the library.
        /// </summary>
        internal static string? Describe(Func<string, Field?> currentField, FieldProxy[] proposed)
        {
            var changes = Diff(currentField, proposed);
            if (changes.Count == 0)
                return null;
            if (changes.Count == 1)
                return $"{changes[0].Field} {changes[0].Text}";

            // Grouped by what was done rather than to which field: "4 set to searchable" is the
            // shape of the change, and the field names are in the console for anyone who needs them.
            var grouped = changes
                .GroupBy(c => c.Kind)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => $"{g.Count()} {g.Key}");
            return $"{changes.Count} field changes — {string.Join(", ", grouped)}";
        }

        /// <summary>What was done to one field: the sentence, and the wording it groups under.</summary>
        private readonly record struct Change(string Field, string Text, string Kind);

        private static List<Change> Diff(Func<string, Field?> currentField, FieldProxy[] proposed)
        {
            var changes = new List<Change>();

            foreach (var wanted in proposed ?? [])
            {
                var field = currentField(wanted.FieldName);
                if (field == null) continue;   // unknown field: SetFieldConfiguration reports it

                // A null on the proxy means "leave this alone", so only a stated value can differ.
                Flag(wanted.Searchable, field.Searchable, "searchable");
                Flag(wanted.Filterable, field.Filterable, "filterable");
                Flag(wanted.Facetable, field.Facetable, "facetable");
                Flag(wanted.Sortable, field.Sortable, "sortable");
                Flag(wanted.WordIndexing, field.WordIndexing, "word indexed");
                Flag(wanted.Embeddable, field.Embeddable, "embeddable");
                Flag(wanted.PreloadFilters, field.PreloadFilters, "preloaded");
                Flag(wanted.HighResolution, field.HighResolution, "high resolution");

                Number(wanted.Weight, field.Weight, "weight");
                Number(wanted.BM25b, field.BM25b, "BM25 b");
                Number(wanted.BM25k1, field.BM25k1, "BM25 k1");

                void Flag(bool? asked, bool now, string role)
                {
                    if (asked is not bool value || value == now) return;
                    changes.Add(value
                        ? new Change(wanted.FieldName, $"is set to {role}", $"set to {role}")
                        : new Change(wanted.FieldName, $"is no longer {role}", $"no longer {role}"));
                }

                void Number(float? asked, float now, string what)
                {
                    // Floats arrive off a JSON round trip, so compare with a tolerance rather than
                    // reporting a change nobody made.
                    if (asked is not float value || Math.Abs(value - now) < 0.0001f) return;
                    // Invariant, like the rest of the monitor: a weight should not read "2,5" in
                    // the terminal and "2.5" in a log because of the server's locale.
                    changes.Add(new Change(wanted.FieldName,
                        string.Create(CultureInfo.InvariantCulture, $"{what} {now:0.##} → {value:0.##}"),
                        $"{what} changed"));
                }
            }

            return changes;
        }

        /// <summary>True when the change cannot be applied to the live engine as it stands.</summary>
        public static bool NeedsRebuild(DocumentFields current, FieldProxy[] proposed) =>
            current.RequiresReindex(proposed) || current.RequiresReload(proposed);

        /// <summary>Applies a change that needs no rebuild to the live engine, and saves it: the
        /// library only persists by itself when Searchable or Sortable changes. Returns the name of
        /// a field that does not exist, or null.</summary>
        public static string? ApplyInPlace(IServerSearchEngine engine, FieldProxy[] proposed)
        {
            var unknown = engine.SetFieldConfiguration(proposed);
            if (unknown == null && engine.DocumentFields is { } fields)
                engine.Persistence?.SaveDocumentFields(fields.GetSerialized());
            return unknown;
        }
    }
}
