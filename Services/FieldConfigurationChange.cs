using Indx.Api;

namespace IndxServer.Services
{
    /// <summary>
    /// Whether a field configuration change can be applied to the live engine, or has to go
    /// through a rebuild on a shadow engine.
    ///
    /// The library's <see cref="DocumentFields.RequiresReindex"/> answers for the inverted index:
    /// Searchable, WordIndexing, Embeddable and the BM25 parameters need a rebuild; Filterable,
    /// Facetable and Sortable "take effect at query time". That last part is only true for a
    /// field that already had a role when the data was loaded. A document keeps a position index
    /// for the fields in use at that moment and for no others, so a field getting its FIRST role
    /// has no positions in any live document: the flag is set, and facets stay empty, filters
    /// match nothing, with no error. This was found on Millum, where two fields were made
    /// Facetable after a wake and never produced a facet.
    ///
    /// Such a change is therefore sent through the shadow as well. The shadow is a clone, and it
    /// rebuilds the positions for newly used fields (the fix in LoadFromClonedDocuments; without
    /// that fix the shadow has the same gap). See Notes/Jens/clone-drops-newly-used-fields.
    ///
    /// Two more things the library leaves to the caller on the in-place path, both found the same
    /// evening:
    ///
    /// - ANY change to Facetable needs the rebuild, first role or not. DocumentFields caches its
    ///   facetable field list on first use and never refreshes it, and the facet code reads that
    ///   list, so a Facetable flag set on a live engine is not seen. A clone starts with a fresh
    ///   DocumentFields and so a fresh list.
    /// - Filterable, Facetable, Sortable and Weight are plain properties on Field; only Searchable
    ///   raises the change notification that persists the configuration. So a change applied in
    ///   place was never written to the store and was gone after the next reload.
    ///   <see cref="ApplyInPlace"/> saves it explicitly.
    ///
    /// Known limit: "in use" is read from the flags. A field whose flag was set inline by an
    /// older build has the flag and still no positions, and no later save will notice. A reload
    /// from the store (hibernate and wake) repairs that.
    /// </summary>
    public static class FieldConfigurationChange
    {
        public static bool NeedsRebuild(DocumentFields current, FieldProxy[] proposed) =>
            current.RequiresReindex(proposed) || ChangesFacetable(current, proposed) || BringsNewFieldIntoUse(current, proposed);

        /// <summary>True when the proposal turns Facetable on or off for any field.</summary>
        public static bool ChangesFacetable(DocumentFields current, FieldProxy[] proposed) =>
            proposed.Any(cfg => cfg.FieldName != null && cfg.Facetable is { } wanted
                                && current.GetField(cfg.FieldName) is { } f && f.Facetable != wanted);

        /// <summary>Applies a change that needs no rebuild to the live engine, and saves it: the
        /// library only persists by itself when Searchable changes. Returns the name of a field
        /// that does not exist, or null.</summary>
        public static string? ApplyInPlace(IServerSearchEngine engine, FieldProxy[] proposed)
        {
            var unknown = engine.SetFieldConfiguration(proposed);
            if (unknown == null && engine.DocumentFields is { } fields)
                engine.Persistence?.SaveDocumentFields(fields.GetSerialized());
            return unknown;
        }

        /// <summary>True when the proposal gives a role to a field that has none now.</summary>
        public static bool BringsNewFieldIntoUse(DocumentFields current, FieldProxy[] proposed)
        {
            foreach (var cfg in proposed)
            {
                var f = cfg.FieldName == null ? null : current.GetField(cfg.FieldName);
                if (f == null) continue;

                // Mirrors DocumentFields.GetUsedFields, which decides what a load keeps positions for.
                bool usedNow = f.Searchable || f.Filterable || f.Facetable || f.Sortable
                               || f.PreloadFilters || f.WordIndexing
                               || f.Name == current.NameOfDocumentKeyField;
                if (usedNow) continue;

                bool usedAfter = (cfg.Searchable ?? false) || (cfg.Filterable ?? false) || (cfg.Facetable ?? false)
                                 || (cfg.Sortable ?? false) || (cfg.PreloadFilters ?? false) || (cfg.WordIndexing ?? false);
                if (usedAfter) return true;
            }
            return false;
        }
    }
}
