using System.Text.Json;
using System.Text.Json.Nodes;

namespace IndxServer.Services.Webhooks
{
    /// <summary>
    /// What held batches hold, for a person deciding about them: how many deletions and other
    /// changes, whether a full set waits, and the names of the documents concerned (the deletions
    /// first, read from the documents Indx still holds, since a deletion carries only an id).
    /// </summary>
    public sealed record WebhookHeldSummary(int Deletes, int Changes, int SyncDocuments, IReadOnlyList<string> Names, int MoreNames)
    {
        public const int NamesShown = 5;

        /// <summary>The fields a document's name is looked for in, in order, before its id.</summary>
        private static readonly string[] NameFields = ["title", "name", "displayName", "heading", "label", "headline"];

        public bool HasSync => SyncDocuments > 0;
        public bool OnlyDeletes => Deletes > 0 && Changes == 0 && !HasSync;

        /// <param name="hook">The webhook the batches were held for.</param>
        /// <param name="batches">The held batches to summarise.</param>
        /// <param name="keys">The registry: the sender's ids to Indx keys.</param>
        /// <param name="document">An indexed document by key, or null (gone, or the dataset not loaded).</param>
        public static WebhookHeldSummary Of(Webhook hook, IReadOnlyList<WebhookHeldBatch> batches,
            Func<IEnumerable<string>, Dictionary<string, long>> keys, Func<long, string?> document)
        {
            int deletes = 0, changes = 0, syncDocuments = 0;
            var deleted = new List<string>();
            var changed = new List<string>();
            foreach (var batch in batches)
            {
                if (batch.Kind == WebhookKinds.Sync) { syncDocuments += batch.Events; continue; }
                List<JsonObject> events;
                try { events = WebhookPlanner.Events(JsonNode.Parse(batch.Body)); }
                catch (Exception ex) when (ex is JsonException or FormatException) { changes += batch.Events; continue; }
                foreach (var e in events)
                {
                    var id = e[hook.IdField]?.ToString();
                    if (e[WebhookPlanner.EventField]?.ToString() == "delete")
                    {
                        deletes++;
                        if (id != null) deleted.Add(id);
                    }
                    else
                    {
                        changes++;
                        if ((NameIn(e) ?? id) is { } name) changed.Add(name);
                    }
                }
            }

            // A deletion carries only the id: the name is the indexed document's, when it can be read.
            var found = deleted.Count == 0 ? [] : keys(deleted.Distinct());
            var names = deleted.Distinct().Select(id =>
                    found.TryGetValue(id, out var key) && document(key) is { } json && NameIn(JsonNode.Parse(json) as JsonObject) is { } name ? name : id)
                .Concat(changed.Distinct())
                .ToList();
            return new(deletes, changes, syncDocuments, names.Take(NamesShown).ToList(), Math.Max(0, names.Count - NamesShown));
        }

        private static string? NameIn(JsonObject? doc)
        {
            if (doc == null) return null;
            foreach (var field in NameFields)
                if (doc[field] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s)) return s.Trim();
            return null;
        }
    }
}
