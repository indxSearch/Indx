using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace IndxServer.Mcp
{
    /// <summary>One declarative search condition: exact <see cref="Value"/> OR numeric <see cref="Min"/>/<see cref="Max"/> range.</summary>
    public class McpFilter
    {
        public string Field { get; set; } = "";
        public string? Value { get; set; }
        public double? Min { get; set; }
        public double? Max { get; set; }
    }

    /// <summary>A dataset the caller can reach, with light status.</summary>
    public class McpDatasetSummary
    {
        public string Team { get; set; } = "";
        public string Dataset { get; set; } = "";
        public string Role { get; set; } = "";
        public long DocumentCount { get; set; }
        public string State { get; set; } = "";
    }

    /// <summary>Inclusive numeric bounds for a numeric field.</summary>
    public class McpRange
    {
        public double Min { get; set; }
        public double Max { get; set; }
    }

    /// <summary>A configured (queryable) field plus value hints for building precise filters.</summary>
    public class McpFieldInfo
    {
        public string Name { get; set; } = "";
        public string Type { get; set; } = "";
        public bool WordMatching { get; set; }
        public bool PatternMatching { get; set; }
        public bool Filterable { get; set; }
        public bool Facetable { get; set; }
        public bool Sortable { get; set; }

        /// <summary>Distinct values present (facetable fields), so the agent filters with real values, not guesses.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public object[]? Values { get; set; }

        /// <summary>Observed numeric bounds (numeric facetable fields).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public McpRange? Range { get; set; }
    }

    /// <summary>The queryable surface of a dataset, for an agent to build effective queries.</summary>
    public class McpDatasetSchema
    {
        public string Team { get; set; } = "";
        public string Dataset { get; set; } = "";

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Description { get; set; }

        public long DocumentCount { get; set; }
        public string State { get; set; } = "";
        public List<McpFieldInfo> Fields { get; set; } = new();

        /// <summary>One full document, so the agent sees the real record shape (incl. non-queryable fields).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public JsonNode? Sample { get; set; }

        /// <summary>The dataset's and the team's notes, by name, without content: read with get_notes.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<McpNote>? Notes { get; set; }
    }

    /// <summary>A note an agent reads beside the search: its name, language and what it is, and its
    /// content where get_notes returns it.</summary>
    public class McpNote
    {
        public string Name { get; set; } = "";
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Language { get; set; }
        /// <summary>"dataset", "team" or "record".</summary>
        public string Of { get; set; } = "";
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Description { get; set; }
        public string ContentType { get; set; } = "";
        public int Length { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Content { get; set; }
        /// <summary>The content was cut to keep the answer within the agent's context.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool Truncated { get; set; }
    }

    /// <summary>One ranked hit: full document + relevance score.</summary>
    public class McpSearchHit
    {
        public long Key { get; set; }
        public int Score { get; set; }
        public JsonNode? Document { get; set; }
    }

    /// <summary>Search response: ranked hits, optional facets, truncation note.</summary>
    public class McpSearchResult
    {
        /// <summary>The hits returned.</summary>
        public int Count { get; set; }
        /// <summary>How many documents hold every query word, beyond the hits returned; 0 for a
        /// fallback.</summary>
        public int Total { get; set; }
        /// <summary>True when no document held every word and the hits are the nearest (only with
        /// broaden, which turns the fallback on).</summary>
        public bool IsFallback { get; set; }
        public List<McpSearchHit> Hits { get; set; } = new();

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, Dictionary<string, int>>? Facets { get; set; }

        public bool Truncated { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Note { get; set; }
    }
}
