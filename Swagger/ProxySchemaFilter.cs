using System.Text.Json.Nodes;
using Indx.Api;
using Indx.Http;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace IndxServer.Swagger
{
    /// <summary>
    /// Swagger schema filter that adds example values for proxy classes.
    ///
    /// The hashString examples are deliberately PLACEHOLDERS, not real tokens. The token is opaque
    /// by contract - a client obtains one from a filter endpoint and passes it back - and a
    /// realistic-looking example invites readers to infer a format and compose against it. Its
    /// internal form is documented nowhere client-facing, on purpose; see Notes/archive/backlog-2026-10-20.md item 9
    /// for why that policy was chosen over documenting it.
    /// </summary>
    public class ProxySchemaFilter : ISchemaFilter
    {
        /// <summary>
        /// Applies example values to proxy class schemas
        /// </summary>
        /// <param name="schema">The schema to modify</param>
        /// <param name="context">The schema filter context containing type information</param>
        public void Apply(IOpenApiSchema schema, SchemaFilterContext context)
        {
            // Microsoft.OpenApi 2.x hands the filter the schema as an interface, read-only; the
            // generator's own schemas are the concrete class, and only they can carry an example.
            if (schema is not OpenApiSchema own)
                return;

            // RangeFilterProxy examples
            if (context.Type == typeof(RangeFilterProxy))
            {
                own.Example = new JsonObject
                {
                    ["fieldName"] = "price",
                    ["lowerLimit"] = 10.0,
                    ["upperLimit"] = 100.0
                };
            }
            // ValueFilterProxy examples
            else if (context.Type == typeof(ValueFilterProxy))
            {
                own.Example = new JsonObject
                {
                    ["fieldName"] = "category",
                    ["value"] = "electronics",
                    ["isCaseSensitive"] = false
                };
                // The property is 'object' in C#, which the generator renders as an empty schema.
                // Say what it takes.
                if (own.Properties != null && own.Properties.TryGetValue("value", out var value) && value is OpenApiSchema valueSchema)
                    valueSchema.Description = "A string, number or boolean, compared as text against the stored value.";
            }
            // BoostProxy examples
            else if (context.Type == typeof(BoostProxy))
            {
                own.Example = new JsonObject
                {
                    ["boostStrength"] = (int)BoostStrength.Med,
                    ["filterProxy"] = new JsonObject
                    {
                        ["hashString"] = "example-filter-token-12345"
                    }
                };
            }
            // CombinedFilterProxy examples
            else if (context.Type == typeof(CombinedFilterProxy))
            {
                own.Example = new JsonObject
                {
                    ["filters"] = new JsonArray
                    {
                        new JsonObject { ["hashString"] = "example-filter-token-12345" },
                        new JsonObject { ["hashString"] = "example-filter-token-67890" },
                    },
                    ["useAndOperation"] = true
                };
            }
            // FilterProxy examples
            else if (context.Type == typeof(FilterProxy))
            {
                own.Example = new JsonObject
                {
                    ["hashString"] = "example-filter-token-12345"
                };
            }
        }
    }
}
