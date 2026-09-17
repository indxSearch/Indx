using System.Globalization;
using System.Security.Claims;
using System.Text.Json.Nodes;
using Indx.Api;
using Indx.Http;
using IndxServer.Models;
using IndxServer.Services;
using ModelContextProtocol.Server;

namespace IndxServer.Mcp
{
    /// <summary>
    /// Read-only MCP tools over the Indx search engine. Each call is authenticated by the bearer
    /// token (an API key) on the /mcp request; access is scoped to the caller's teams. Searches run
    /// in-process through <see cref="IndxServerInternalApi.Manager"/>, so the dataset's saved boost
    /// rules apply automatically, and hibernated datasets auto-wake (ResolveEngine).
    /// </summary>
    [McpServerToolType]
    public sealed class IndxMcpTools(
        IHttpContextAccessor http,
        TeamService teams,
        DatasetMetadataStore metadata)
    {
        // Default match semantics for agents: precise near-exact only (Coverage on, pattern
        // matches off). A query with no near-exact hit returns nothing rather than fuzzy noise.
        private const int DefaultLimit = 10;
        private const int MaxResponseBytes = 80_000; // size guardrail for the context window
        private const int MaxFacetValues = 25;

        // ── Tools ─────────────────────────────────────────────────────────────

        [McpServerTool(Name = "list_datasets", UseStructuredContent = false, ReadOnly = true)]
        [System.ComponentModel.Description("List the search datasets the caller can access, with team, role, document count and state.")]
        public async Task<McpDatasetSummary[]> ListDatasets()
        {
            var userId = RequireUserId();
            var scope = Scope();
            var result = new List<McpDatasetSummary>();
            foreach (var (team, role) in await teams.GetTeamsForUserAsync(userId))
            {
                if (scope != null && !scope.AllowsTeam(team.Id)) continue;
                var ownerKey = team.Id.ToString();
                foreach (var ds in IndxServerInternalApi.Manager.GetTeamDataSets(ownerKey))
                {
                    if (scope != null && !scope.AllowsDataset(ds)) continue;
                    var ka = IndxServerInternalApi.Manager.GetKeepAliveInfo(ds, ownerKey);
                    result.Add(new McpDatasetSummary
                    {
                        Team = team.Name,
                        Dataset = ds,
                        Role = role,
                        DocumentCount = ka.RecordCount,
                        State = ka.Ready ? "Ready" : "Asleep",
                    });
                }
            }
            return result.ToArray();
        }

        [McpServerTool(Name = "describe_dataset", UseStructuredContent = false, ReadOnly = true)]
        [System.ComponentModel.Description("Describe a dataset's queryable surface: configured fields (with capabilities), value hints " +
                     "(distinct values for facetable fields, numeric ranges), an owner description, and a sample document. " +
                     "Call this before search to know which fields you can search/filter/sort on and what values are valid.")]
        public async Task<McpDatasetSchema> DescribeDataset(
            [System.ComponentModel.Description("Team name that owns the dataset.")] string team,
            [System.ComponentModel.Description("Dataset name.")] string dataset)
        {
            var ownerKey = await ResolveOwnerKey(team, dataset, ApiKeyLevel.Read);
            var engine = ResolveEngine(dataset, ownerKey);

            var ka = IndxServerInternalApi.Manager.GetKeepAliveInfo(dataset, ownerKey);
            var schema = new McpDatasetSchema
            {
                Team = team,
                Dataset = dataset,
                Description = NullIfEmpty(metadata.Load(ownerKey, dataset)),
                DocumentCount = ka.RecordCount,
                State = engine.Status.SystemState.ToString(),
            };

            var fieldCfg = engine.GetFieldConfiguration();
            // Configured fields only (those with at least one role) — the queryable surface.
            var configured = fieldCfg
                .Where(f => (f.Searchable ?? false) || (f.Filterable ?? false) || (f.Facetable ?? false) || (f.Sortable ?? false))
                .ToList();

            // One match-all faceted search → facet value hints for all facetable fields + a sample doc.
            var facetable = configured.Where(f => f.Facetable == true).Select(f => f.FieldName).ToHashSet();
            Result? probe = TryMatchAll(dataset, ownerKey, fieldCfg, withFacets: facetable.Count > 0);

            foreach (var f in configured)
            {
                var info = new McpFieldInfo
                {
                    Name = f.FieldName,
                    Type = (f.FieldType ?? "String").ToLowerInvariant(),
                    Searchable = f.Searchable ?? false,
                    Filterable = f.Filterable ?? false,
                    Facetable = f.Facetable ?? false,
                    Sortable = f.Sortable ?? false,
                };

                if (f.Facetable == true && probe?.Facets != null
                    && probe.Facets.TryGetValue(f.FieldName, out var facetVals) && facetVals.Length > 0)
                {
                    var labels = facetVals.Take(MaxFacetValues).Select(v => v.Key).ToArray();
                    info.Values = labels.Select(l => Coerce(l, info.Type)).ToArray();
                    if (info.Type == "number")
                    {
                        var nums = labels
                            .Select(l => double.TryParse(l, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : (double?)null)
                            .Where(d => d.HasValue).Select(d => d!.Value).ToList();
                        if (nums.Count > 0) info.Range = new McpRange { Min = nums.Min(), Max = nums.Max() };
                    }
                }
                schema.Fields.Add(info);
            }

            // Sample document from the probe (first record).
            if (probe is { Records.Length: > 0 })
                schema.Sample = ParseJson(engine.GetJsonDataOfKey(probe.Records[0].DocumentKey));

            return schema;
        }

        [McpServerTool(Name = "search", UseStructuredContent = false, ReadOnly = true)]
        [System.ComponentModel.Description("Search a dataset and return ranked documents with relevance scores. Matching is precise by " +
                     "default (near-exact only, incl. typo tolerance) — an empty result means nothing matches well, " +
                     "which is a trustworthy 'not found' (don't retry with looser wording unless you set broaden=true). " +
                     "Saved boost rules are applied. Use filters for structured constraints on filterable fields " +
                     "(get valid fields/values from describe_dataset).")]
        public async Task<McpSearchResult> Search(
            [System.ComponentModel.Description("Team name that owns the dataset.")] string team,
            [System.ComponentModel.Description("Dataset name.")] string dataset,
            [System.ComponentModel.Description("Free-text query. Matches searchable fields.")] string query,
            [System.ComponentModel.Description("Structured constraints (AND-combined). Each: {field, value} for exact match, or {field, min, max} for a numeric range. Fields must be filterable.")] McpFilter[]? filters = null,
            [System.ComponentModel.Description("Max documents to return (default 10).")] int limit = DefaultLimit,
            [System.ComponentModel.Description("If set, return only these top-level fields from each document.")] string[]? fields = null,
            [System.ComponentModel.Description("Set true to include broad fuzzy/pattern matches (lower precision). Default false = near-exact only.")] bool broaden = false,
            [System.ComponentModel.Description("Set true to also return facet counts (for facetable fields) to refine the next query.")] bool facets = false)
        {
            var ownerKey = await ResolveOwnerKey(team, dataset, ApiKeyLevel.Search);
            var engine = ResolveEngine(dataset, ownerKey);

            // An empty query is a browse: "everything matching the filters". The engine only
            // serves it with facets on, so enable them regardless of what the agent asked for —
            // otherwise a filter-only call returns zero hits and looks like a trustworthy no-match.
            var isBrowse = string.IsNullOrWhiteSpace(query);
            var queryProxy = new QueryProxy
            {
                Text = query ?? "",
                MaxNumberOfRecordsToReturn = Math.Clamp(limit, 1, 100),
                EnableCoverage = true,
                CoverageSetup = new CoverageSetup { IncludePatternMatches = broaden },
                EnableBoost = true,
                EnableFacets = facets || isBrowse,
            };

            if (filters is { Length: > 0 })
            {
                Filter? combined = null;
                foreach (var c in filters)
                {
                    var built = FilterConditionBuilder.Build(engine, c.Field, c.Value, c.Min, c.Max);
                    if (built == null)
                        throw new McpToolException($"Filter field '{c.Field}' is not filterable, or the condition is empty.");
                    combined = combined == null ? built : combined & built;
                }
                if (combined != null)
                    queryProxy.Filter = new FilterProxy(combined.SerializedKey);
            }

            var res = IndxServerInternalApi.Manager.Search(queryProxy, dataset, ownerKey);
            return ShapeResult(engine, res, fields);
        }

        [McpServerTool(Name = "get_document", UseStructuredContent = false, ReadOnly = true)]
        [System.ComponentModel.Description("Fetch the full JSON document for a specific document key in a dataset.")]
        public async Task<JsonNode?> GetDocument(
            [System.ComponentModel.Description("Team name that owns the dataset.")] string team,
            [System.ComponentModel.Description("Dataset name.")] string dataset,
            [System.ComponentModel.Description("Document key (from a search hit).")] long key)
        {
            var ownerKey = await ResolveOwnerKey(team, dataset, ApiKeyLevel.Search);
            var engine = ResolveEngine(dataset, ownerKey);
            return ParseJson(engine.GetJsonDataOfKey(key));
        }

        [McpServerTool(Name = "get_synonyms", UseStructuredContent = false, ReadOnly = true)]
        [System.ComponentModel.Description("Get a dataset's synonym list (an experimental feature), or an empty entries list when it has none. " +
                     "Searches expand through these entries: when the query matches an entry, its terms are appended to the " +
                     "query text before scoring, so this explains why a search matched more than its literal words. " +
                     "Multidirectional entries expand from any of their terms; OneWay entries expand only from their Source " +
                     "term. Read-only — synonyms are edited in the portal or via PUT .../synonyms.")]
        public async Task<JsonNode?> GetSynonyms(
            [System.ComponentModel.Description("Team name that owns the dataset.")] string team,
            [System.ComponentModel.Description("Dataset name.")] string dataset)
        {
            var ownerKey = await ResolveOwnerKey(team, dataset, ApiKeyLevel.Read);
            if (IndxServerInternalApi.Manager.ResolveEngine(dataset, ownerKey) == null)
                throw new McpToolException($"Dataset '{dataset}' not found.");
            var list = IndxServerInternalApi.Manager.GetSynonyms(dataset, ownerKey);
            // A bare null serialises to no content at all on the MCP wire, which agents read as an
            // empty (failed) response. Return an explicit, parseable "no list" object instead.
            return list == null
                ? new JsonObject { ["entries"] = new JsonArray(), ["note"] = "This dataset has no synonym list." }
                : ParseJson(list.GetSerialized());
        }

        // ── Setup tools ───────────────────────────────────────────────────────
        // The three tools above answer "what can I search"; these answer "why is it not working
        // yet", which is the question during setup. They deliberately do NOT go through
        // ResolveEngine: that throws unless the dataset is Ready, and a dataset that is not Ready
        // is exactly when an agent needs to look.

        [McpServerTool(Name = "get_status", UseStructuredContent = false, ReadOnly = true)]
        [System.ComponentModel.Description("Dataset state and health: whether it is indexed and ready, document count, scoring mode, " +
                     "any error message, and flags for truncated text or a refused query. Call this when a dataset is " +
                     "not behaving as expected, or while waiting for an index build to finish.")]
        public async Task<JsonObject> GetStatus(
            [System.ComponentModel.Description("Team name that owns the dataset.")] string team,
            [System.ComponentModel.Description("Dataset name.")] string dataset)
        {
            var ownerKey = await ResolveOwnerKey(team, dataset, ApiKeyLevel.Read);
            var status = IndxServerInternalApi.Manager.GetState(dataset, ownerKey)
                ?? throw new McpToolException($"Dataset '{dataset}' not found.");

            var o = new JsonObject
            {
                ["dataset"] = dataset,
                ["team"] = team,
                ["state"] = status.SystemState.ToString(),
                ["ready"] = status.SystemState == SystemState.Ready,
                ["documentCount"] = status.DocumentCount,
                ["secondsToIndex"] = status.SecondsToIndex,
                ["lastIndexBuildUtc"] = status.TimeOfLastIndexBuild == default
                    ? null : status.TimeOfLastIndexBuild.ToString("o", CultureInfo.InvariantCulture),
                ["shadowBuildInProgress"] = IndxServerInternalApi.Manager.IsShadowBuildInProgress(dataset, ownerKey),
            };

            if (!string.IsNullOrWhiteSpace(status.ErrorMessage)) o["errorMessage"] = status.ErrorMessage;
            if (status.TooLongSearchText) o["tooLongSearchText"] = true;
            if (status.IndexedTextTruncated) o["indexedTextTruncated"] = true;
            if (status.InvalidState) o["invalidState"] = true;
            if (status.InvalidArgument) o["invalidArgument"] = true;
            if (status.UnrecoverableErrors.Count > 0)
                o["unrecoverableErrors"] = new JsonArray(status.UnrecoverableErrors
                    .Select(e => (JsonNode?)e.ToString()).ToArray());

            // Only readable once the engine exists; skipped rather than failing the whole call.
            try
            {
                var engine = IndxServerInternalApi.Manager.ResolveEngine(dataset, ownerKey);
                if (engine is SearchEngine se) o["scoringMode"] = se.ScoringMode.ToString();
            }
            catch { /* state is the answer here; a scoring mode we cannot read is not an error */ }

            o["nextStep"] = status.SystemState switch
            {
                SystemState.Ready => "Ready to search.",
                SystemState.Indexing => "An index build is running. Poll this tool until state is Ready.",
                _ => $"State is {status.SystemState}. A dataset becomes searchable after analyze, load and index have all run.",
            };
            return o;
        }

        [McpServerTool(Name = "get_field_configuration", UseStructuredContent = false, ReadOnly = true)]
        [System.ComponentModel.Description("The full field configuration, including fields that are currently switched off: every " +
                     "capability flag, weight and BM25 parameter, whether a field was ever blank or missing, and a sample " +
                     "of its real content. Use this when configuring a dataset; use describe_dataset when writing a query.")]
        public async Task<JsonObject> GetFieldConfigurationTool(
            [System.ComponentModel.Description("Team name that owns the dataset.")] string team,
            [System.ComponentModel.Description("Dataset name.")] string dataset)
        {
            var ownerKey = await ResolveOwnerKey(team, dataset, ApiKeyLevel.Read);
            var engine = IndxServerInternalApi.Manager.ResolveEngine(dataset, ownerKey)
                ?? throw new McpToolException($"Dataset '{dataset}' not found.");
            var cfg = engine.GetFieldConfiguration();

            var fields = new JsonArray();
            foreach (var f in cfg)
            {
                var jf = new JsonObject
                {
                    ["name"] = f.FieldName,
                    ["type"] = f.FieldType,
                    ["isArray"] = f.IsArray,
                    ["searchable"] = f.Searchable,
                    ["filterable"] = f.Filterable,
                    ["facetable"] = f.Facetable,
                    ["sortable"] = f.Sortable,
                    ["weight"] = f.Weight,
                    ["bm25b"] = f.BM25b,
                    ["bm25k1"] = f.BM25k1,
                };
                // Optional means some document lacked a value; a null sample means no document ever
                // had one. Both change whether a field is worth making searchable, and neither is
                // visible from the type alone.
                if (f.Optional == true) jf["optional"] = true;
                if (!string.IsNullOrEmpty(f.SampleValue)) jf["sample"] = f.SampleValue;
                else jf["blank"] = true;
                fields.Add(jf);
            }

            var searchable = cfg.Count(f => f.Searchable == true);
            return new JsonObject
            {
                ["dataset"] = dataset,
                ["fields"] = fields,
                ["searchableFieldCount"] = searchable,
                ["note"] = searchable == 0
                    ? "No field is searchable, so every search returns nothing. Make at least one text field searchable, then re-index."
                    : "Weight is a float (default 1.0); BM25b must be within [0, 1] and BM25k1 non-negative, or the engine refuses the configuration.",
            };
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private string RequireUserId() =>
            http.HttpContext?.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? throw new McpToolException("Not authenticated.");

        private ApiKeyScope? Scope() => ApiKeyScope.FromPrincipal(http.HttpContext?.User);

        /// <summary>
        /// The team's owner key, after the same checks the HTTP API applies: membership, then a scoped
        /// key's team, datasets and level (ApiKeyScopeFilter does this for MVC; MCP tools are not MVC
        /// actions, so every tool comes through here). A key outside its scope gets the not-found
        /// answer rather than a different one, so it cannot probe for teams or datasets.
        /// </summary>
        private async Task<string> ResolveOwnerKey(string team, string dataset, ApiKeyLevel required)
        {
            var userId = RequireUserId();
            var match = (await teams.GetTeamsForUserAsync(userId))
                .FirstOrDefault(t => string.Equals(t.Team.Name, team, StringComparison.OrdinalIgnoreCase));
            var scope = Scope();
            if (match.Team == null || (scope != null && !scope.AllowsTeam(match.Team.Id)))
                throw new McpToolException($"Team '{team}' not found or not accessible.");
            if (scope != null && !scope.AllowsDataset(dataset))
                throw new McpToolException($"Dataset '{dataset}' not found.");
            if (scope != null && !scope.AllowsLevel(required))
                throw new McpToolException($"This API key is limited to {ApiKeyScope.Describe(scope.Level)}; this tool needs at least {ApiKeyScope.Describe(required)}.");
            return match.Team.Id.ToString();
        }

        private static IServerSearchEngine ResolveEngine(string dataset, string ownerKey)
        {
            var engine = IndxServerInternalApi.Manager.ResolveEngine(dataset, ownerKey); // auto-wakes if hibernated
            if (engine == null)
                throw new McpToolException($"Dataset '{dataset}' not found.");
            if (engine.Status.SystemState != SystemState.Ready)
                throw new McpToolException($"Dataset '{dataset}' is not ready (state: {engine.Status.SystemState}).");
            return engine;
        }

        /// <summary>Match-all via an empty query sorted by any sortable field (empty text alone returns nothing).</summary>
        private static Result? TryMatchAll(string dataset, string ownerKey, FieldProxy[] fieldCfg, bool withFacets)
        {
            var sortable = fieldCfg.FirstOrDefault(f => f.Sortable == true)?.FieldName;
            if (sortable == null) return null; // no sortable field → can't enumerate; skip hints/sample
            var q = new QueryProxy
            {
                Text = "",
                MaxNumberOfRecordsToReturn = 1,
                SortBy = sortable,
                SortAscending = true,
                EnableFacets = withFacets,
                EnableCoverage = true,
            };
            return IndxServerInternalApi.Manager.Search(q, dataset, ownerKey);
        }

        private static McpSearchResult ShapeResult(IServerSearchEngine engine, Result res, string[]? fields)
        {
            var outp = new McpSearchResult();
            int bytes = 0;
            foreach (var rec in res.Records)
            {
                var doc = ParseJson(engine.GetJsonDataOfKey(rec.DocumentKey));
                if (doc != null && fields is { Length: > 0 }) doc = Project(doc, fields);
                int sz = doc?.ToJsonString().Length ?? 0;
                if (bytes + sz > MaxResponseBytes && outp.Hits.Count > 0)
                {
                    outp.Truncated = true;
                    outp.Note = $"Truncated to {outp.Hits.Count} hits to fit the response size limit.";
                    break;
                }
                bytes += sz;
                outp.Hits.Add(new McpSearchHit { Key = rec.DocumentKey, Score = rec.Score, Document = doc });
            }
            outp.Count = outp.Hits.Count;

            if (res.Facets != null)
            {
                outp.Facets = res.Facets.ToDictionary(
                    kv => kv.Key,
                    kv => kv.Value.ToDictionary(p => p.Key, p => p.Value));
            }
            return outp;
        }

        private static JsonNode? Project(JsonNode doc, string[] fields)
        {
            if (doc is not JsonObject obj) return doc;
            var keep = new JsonObject();
            foreach (var f in fields)
                if (obj.TryGetPropertyValue(f, out var v))
                    keep[f] = v?.DeepClone();
            return keep;
        }

        private static JsonNode? ParseJson(string? json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try { return JsonNode.Parse(json); } catch { return null; }
        }

        private static object Coerce(string label, string type) => type switch
        {
            "number" => double.TryParse(label, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : label,
            "boolean" => bool.TryParse(label, out var b) ? b : label,
            _ => label,
        };

        private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
    }

    /// <summary>An error surfaced to the MCP client as a tool failure with a clean message.</summary>
    /// <summary>
    /// A tool error the agent should read. Deriving from McpException is what makes the SDK pass
    /// the message to the client; a plain Exception reaches the agent only as "An error occurred
    /// invoking 'tool'", which hid every not-found, not-ready and key-scope reason.
    /// </summary>
    public sealed class McpToolException(string message) : ModelContextProtocol.McpException(message);
}
