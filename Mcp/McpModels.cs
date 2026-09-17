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
        public bool Searchable { get; set; }
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
        public int Count { get; set; }
        public List<McpSearchHit> Hits { get; set; } = new();

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, Dictionary<string, int>>? Facets { get; set; }

        public bool Truncated { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Note { get; set; }
    }

    /// <summary>
    /// One field's requested settings for <c>set_field_configuration</c>. Every property but
    /// <see cref="Field"/> is nullable and means "leave this alone" when omitted, which is the
    /// same replace-semantics the HTTP route uses: passing <c>false</c> switches something off,
    /// passing nothing does not.
    /// </summary>
    public sealed class McpFieldSetting
    {
        /// <summary>The field name, exactly as get_field_configuration reports it.</summary>
        public string Field { get; set; } = string.Empty;

        /// <summary>Include this field's text when matching a query.</summary>
        public bool? Searchable { get; set; }

        /// <summary>Allow filters on this field.</summary>
        public bool? Filterable { get; set; }

        /// <summary>Allow facet counts on this field.</summary>
        public bool? Facetable { get; set; }

        /// <summary>Allow sorting by this field.</summary>
        public bool? Sortable { get; set; }

        /// <summary>Relevance weight. 1.0 is neutral; higher means matches here count for more.</summary>
        public float? Weight { get; set; }

        /// <summary>BM25 length normalisation, within [0, 1].</summary>
        public float? BM25b { get; set; }

        /// <summary>BM25 term saturation, not negative.</summary>
        public float? BM25k1 { get; set; }

        internal Indx.Api.FieldProxy ToProxy() => new()
        {
            FieldName = Field,
            Searchable = Searchable,
            Filterable = Filterable,
            Facetable = Facetable,
            Sortable = Sortable,
            Weight = Weight,
            BM25b = BM25b,
            BM25k1 = BM25k1,
        };
    }
}
