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
