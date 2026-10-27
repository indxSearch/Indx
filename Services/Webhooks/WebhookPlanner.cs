using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IndxServer.Services.Webhooks
{
    /// <summary>What the planner needs to know about the dataset, as it stands: its key field, its
    /// fields' types (a number field takes numbers), its size, and the stored document of a key.</summary>
    public interface IWebhookDataset
    {
        string KeyField { get; }
        IReadOnlyDictionary<string, JsonValueKind> FieldTypes { get; }
        long DocumentCount { get; }
        string? Document(long key);
    }

    /// <summary>What a call comes to: the documents to insert, to update and to delete; the new
    /// entries for the key registry; the events left alone (older than what is indexed, or a delete
    /// of what is not there) and the ones refused, with why. Destructive counts the deletes and the
    /// updates that empty or drop a field the document had, which is what the brake weighs.</summary>
    public sealed class WebhookPlan
    {
        public List<(long Key, string Json)> Inserts { get; } = [];
        public List<(long Key, string Json)> Updates { get; } = [];
        public List<long> Deletes { get; } = [];
        public Dictionary<string, long> NewKeys { get; } = new(StringComparer.Ordinal);
        public List<string> Refusals { get; } = [];
        public List<string> Notes { get; } = [];
        public int Events { get; set; }
        public int Ignored { get; set; }
        public int Destructive { get; set; }
        /// <summary>The whole call was unreadable: nothing in it applies.</summary>
        public string? Error { get; set; }
    }

    /// <summary>What a full sync comes to: the whole set as one JSON array, each document with its
    /// number in <see cref="KeyField"/>; the registry's new entries; the documents refused.</summary>
    public sealed class WebhookSyncPlan
    {
        public string Json { get; set; } = "[]";
        public int Count { get; set; }
        public string KeyField { get; set; } = "";
        public Dictionary<string, long> NewKeys { get; } = new(StringComparer.Ordinal);
        public List<string> Refusals { get; } = [];
        public int Skipped { get; set; }
        public string? Error { get; set; }
    }

    /// <summary>
    /// Turns a webhook call into a <see cref="WebhookPlan"/>, without touching the engine. The body
    /// is an event object, a list of them, or <c>{ "events": [...] }</c>; an event is the document
    /// itself with an <c>event</c> field (insert, update, upsert or delete, and their past tenses;
    /// left out, an upsert). Each event is checked on its own, so one bad document is refused and the
    /// rest of the call still applies; nothing is ever half applied.
    /// </summary>
    public static class WebhookPlanner
    {
        public const int MaxEvents = 1000;
        public const string EventField = "event";
        /// <summary>The field a full sync writes each document's number into, when the dataset has
        /// no key field of its own yet: it becomes the dataset's key field.</summary>
        public const string DefaultKeyField = "indxKey";

        private enum Op { Upsert, Insert, Delete }

        public static WebhookPlan Plan(string body, Webhook hook, IWebhookDataset dataset,
            Func<IEnumerable<string>, Dictionary<string, long>> registry, Func<long, bool> keyTaken)
        {
            var plan = new WebhookPlan();
            if (string.IsNullOrEmpty(dataset.KeyField))
            {
                plan.Error = "The dataset has no key field: set one before its webhooks can apply anything.";
                return plan;
            }
            List<JsonObject> events;
            try { events = Events(JsonNode.Parse(body)); }
            catch (JsonException ex) { plan.Error = $"The body is not JSON: {ex.Message}"; return plan; }
            catch (FormatException ex) { plan.Error = ex.Message; return plan; }
            plan.Events = events.Count;
            if (events.Count > MaxEvents)
            {
                plan.Error = $"{events.Count} events in one call; at most {MaxEvents}.";
                return plan;
            }

            // The ids first, so the registry is asked once for the whole call.
            var ids = new List<(JsonObject Event, string? Id, string? Problem)>();
            foreach (var e in events) ids.Add((e, IdOf(e, hook.IdField, out var problem), problem));
            var direct = dataset.KeyField == hook.IdField;
            var known = registry(ids.Where(x => x.Id != null && !(direct && IsKey(x.Id))).Select(x => x.Id!));
            // Within one call, a later event for the same id sees what an earlier one planned.
            var planned = new Dictionary<long, string?>();

            foreach (var (e, id, problem) in ids)
            {
                if (id == null) { plan.Refusals.Add(problem!); continue; }
                var op = OpOf(e, out var opProblem);
                if (opProblem != null) { plan.Refusals.Add($"{id}: {opProblem}"); continue; }

                long key;
                bool haveKey;
                if (direct && IsKey(id)) { key = long.Parse(id, CultureInfo.InvariantCulture); haveKey = true; }
                else if (known.TryGetValue(id, out key) || plan.NewKeys.TryGetValue(id, out key)) haveKey = true;
                else haveKey = false;

                string? stored = haveKey ? (planned.TryGetValue(key, out var p) ? p : dataset.Document(key)) : null;

                if (op == Op.Delete)
                {
                    if (stored == null) { plan.Ignored++; continue; } // nothing there: fine, and nothing to do
                    plan.Deletes.Add(key);
                    plan.Destructive++;
                    planned[key] = null;
                    continue;
                }

                var doc = (JsonObject)e.DeepClone();
                doc.Remove(EventField);
                if (TypeProblem(doc, dataset.FieldTypes, hook.IdField, dataset.KeyField) is { } typeProblem)
                {
                    plan.Refusals.Add($"{id}: {typeProblem}");
                    continue;
                }
                if (stored != null && hook.VersionField is { } vf && Older(doc[vf], Field(stored, vf)))
                {
                    plan.Ignored++;
                    plan.Notes.Add($"{id}: older than the indexed version ({vf}), left alone");
                    continue;
                }
                if (!haveKey)
                {
                    key = NewKey(id, k => keyTaken(k) || plan.NewKeys.ContainsValue(k) || dataset.Document(k) != null);
                    plan.NewKeys[id] = key;
                }
                doc[dataset.KeyField] = key;
                var json = doc.ToJsonString();
                if (stored == null)
                {
                    plan.Inserts.Add((key, json));
                }
                else
                {
                    if (op == Op.Insert) plan.Notes.Add($"{id}: already there, updated");
                    if (Empties(stored, doc)) plan.Destructive++;
                    plan.Updates.Add((key, json));
                }
                planned[key] = json;
            }
            foreach (var name in NewFields(plan, dataset.FieldTypes)) plan.Notes.Add($"a field the dataset does not have yet: {name}");
            return plan;
        }

        /// <summary>
        /// Plans a full sync: every document the source holds, as <c>[...]</c>, <c>{ "documents": [...] }</c>
        /// or Sanity's <c>{ "result": [...] }</c>. Documents get their numbers as events do; a document
        /// refused (no id, text in a number field) is left out of the set, and so is what the source
        /// leaves out (<paramref name="leavesOut"/>: Sanity's drafts). When an
        /// id comes twice the last one counts.
        /// </summary>
        public static WebhookSyncPlan PlanSync(string body, Webhook hook, IWebhookDataset dataset,
            Func<IEnumerable<string>, Dictionary<string, long>> registry, Func<long, bool> keyTaken, Func<string, bool>? leavesOut = null)
        {
            var plan = new WebhookSyncPlan { KeyField = string.IsNullOrEmpty(dataset.KeyField) ? DefaultKeyField : dataset.KeyField };
            JsonArray list;
            try
            {
                list = JsonNode.Parse(body) switch
                {
                    JsonArray a => a,
                    JsonObject o when o["documents"] is JsonArray a => a,
                    JsonObject o when o["result"] is JsonArray a => a,
                    _ => throw new FormatException("A sync is a list of documents, or { \"documents\": [...] }."),
                };
            }
            catch (JsonException ex) { plan.Error = $"The body is not JSON: {ex.Message}"; return plan; }
            catch (FormatException ex) { plan.Error = ex.Message; return plan; }

            var docs = new List<(string Id, JsonObject Doc)>();
            foreach (var item in list)
            {
                if (item is not JsonObject o) { plan.Error = "Every document in a sync must be a JSON object."; return plan; }
                if (IdOf(o, hook.IdField, out var problem) is not { } id) { plan.Refusals.Add(problem!); continue; }
                if (leavesOut?.Invoke(id) == true) { plan.Skipped++; continue; }
                docs.Add((id, o));
            }

            var direct = plan.KeyField == hook.IdField;
            var known = registry(docs.Where(d => !(direct && IsKey(d.Id))).Select(d => d.Id).Distinct());
            var byKey = new Dictionary<long, JsonObject>();
            var keyOfId = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var (id, o) in docs)
            {
                var doc = (JsonObject)o.DeepClone();
                doc.Remove(EventField);
                if (TypeProblem(doc, dataset.FieldTypes, hook.IdField, plan.KeyField) is { } typeProblem)
                {
                    plan.Refusals.Add($"{id}: {typeProblem}");
                    continue;
                }
                long key;
                if (keyOfId.TryGetValue(id, out key)) { }
                else if (direct && IsKey(id)) key = long.Parse(id, CultureInfo.InvariantCulture);
                else if (!known.TryGetValue(id, out key))
                {
                    key = NewKey(id, k => keyTaken(k) || plan.NewKeys.ContainsValue(k));
                    plan.NewKeys[id] = key;
                }
                keyOfId[id] = key;
                doc[plan.KeyField] = key;
                byKey[key] = doc;
            }
            plan.Count = byKey.Count;
            plan.Json = new JsonArray(byKey.Values.Select(d => (JsonNode?)d).ToArray()).ToJsonString();
            return plan;
        }

        private static List<JsonObject> Events(JsonNode? root)
        {
            var list = root switch
            {
                JsonObject o when o["events"] is JsonArray a => a,
                JsonObject o => new JsonArray(o.DeepClone()),
                JsonArray a => a,
                _ => throw new FormatException("The body must be an event object, a list of them, or { \"events\": [...] }."),
            };
            var events = new List<JsonObject>();
            foreach (var item in list)
            {
                if (item is not JsonObject o) throw new FormatException("Every event must be a JSON object.");
                events.Add(o);
            }
            return events;
        }

        private static string? IdOf(JsonObject e, string idField, out string? problem)
        {
            problem = null;
            var v = e[idField];
            string? id = v is JsonValue value
                ? value.TryGetValue<string>(out var s) ? s.Trim()
                  : value.TryGetValue<long>(out var l) ? l.ToString(CultureInfo.InvariantCulture)
                  : value.TryGetValue<double>(out var d) && d == Math.Floor(d) ? ((long)d).ToString(CultureInfo.InvariantCulture)
                  : null
                : null;
            if (string.IsNullOrEmpty(id))
            {
                problem = $"an event without its id ({idField}), refused";
                return null;
            }
            if (id.Length > 200)
            {
                problem = $"an id longer than 200 characters, refused";
                return null;
            }
            return id;
        }

        private static Op OpOf(JsonObject e, out string? problem)
        {
            problem = null;
            var name = e[EventField] is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim().ToLowerInvariant() : null;
            switch (name)
            {
                case null or "" or "upsert" or "update" or "updated" or "create" or "created" or "publish" or "published":
                    return name is "create" or "created" ? Op.Insert : Op.Upsert;
                case "insert" or "inserted":
                    return Op.Insert;
                case "delete" or "deleted" or "remove" or "removed" or "unpublish" or "unpublished":
                    return Op.Delete;
                default:
                    problem = $"an event called \"{name}\": insert, update or delete";
                    return Op.Upsert;
            }
        }

        private static bool IsKey(string id) => long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var k) && k >= 0;

        /// <summary>A field the dataset holds as a number takes a number (or null). A price that
        /// arrives as "49,99 kr" would otherwise sit in the document as text, and range filters on
        /// price would quietly miss the product.</summary>
        private static string? TypeProblem(JsonObject doc, IReadOnlyDictionary<string, JsonValueKind> types, string idField, string keyField)
        {
            foreach (var (name, value) in doc)
            {
                if (name == keyField || name == idField || value is null) continue;
                if (types.TryGetValue(name, out var kind) && kind == JsonValueKind.Number && value.GetValueKind() != JsonValueKind.Number)
                    return $"{name} must be a number, got {Shorten(value.ToJsonString())}";
            }
            return null;
        }

        private static IEnumerable<string> NewFields(WebhookPlan plan, IReadOnlyDictionary<string, JsonValueKind> types)
        {
            if (types.Count == 0) yield break;
            var seen = new HashSet<string>();
            foreach (var (_, json) in plan.Inserts.Concat(plan.Updates))
                if (JsonNode.Parse(json) is JsonObject o)
                    foreach (var (name, _) in o)
                        if (!types.ContainsKey(name) && seen.Add(name)) yield return name;
        }

        /// <summary>Whether an update empties or drops a field the stored document had: a mass of
        /// those is how an emptied field reaches every document, so the brake counts them.</summary>
        private static bool Empties(string stored, JsonObject incoming)
        {
            if (JsonNode.Parse(stored) is not JsonObject old) return false;
            foreach (var (name, value) in old)
            {
                if (IsEmpty(value)) continue;
                if (!incoming.TryGetPropertyValue(name, out var now) || IsEmpty(now)) return true;
            }
            return false;
        }

        private static bool IsEmpty(JsonNode? v) => v is null
            || v is JsonValue s && s.TryGetValue<string>(out var str) && str.Length == 0
            || v is JsonArray a && a.Count == 0;

        private static JsonNode? Field(string json, string name) => JsonNode.Parse(json) is JsonObject o ? o[name] : null;

        /// <summary>Whether the incoming version is older than the stored one: numbers as numbers,
        /// dates as dates, anything else as text. Without a stored version nothing is older.</summary>
        public static bool Older(JsonNode? incoming, JsonNode? stored)
        {
            if (incoming is not JsonValue a || stored is not JsonValue b) return false;
            if (a.TryGetValue<double>(out var x) && b.TryGetValue<double>(out var y)) return x < y;
            var sa = a.ToString(); var sb = b.ToString();
            if (DateTimeOffset.TryParse(sa, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var da)
                && DateTimeOffset.TryParse(sb, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var db))
                return da < db;
            return string.CompareOrdinal(sa, sb) < 0;
        }

        /// <summary>A numeric key for a text id: the first 62 bits of its SHA-256, so the same id
        /// gets the same key on any server, and the next free one in the rare case it is taken.</summary>
        public static long NewKey(string id, Func<long, bool> taken)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(id));
            var key = (long)(BitConverter.ToUInt64(hash, 0) >> 2);
            while (taken(key)) key = key == long.MaxValue >> 1 ? 1 : key + 1;
            return key;
        }

        private static string Shorten(string s) => s.Length > 40 ? s[..40] + "…" : s;
    }
}
