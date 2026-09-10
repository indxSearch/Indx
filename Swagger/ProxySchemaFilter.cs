using Indx.Api;
using Indx.CloudApi;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace IndxCloudApi.Swagger
{
    /// <summary>
    /// Swagger schema filter that adds example values for proxy classes.
    ///
    /// The hashString examples are deliberately PLACEHOLDERS, not real tokens. The token is opaque
    /// by contract - a client obtains one from a filter endpoint and passes it back - and a
    /// realistic-looking example invites readers to infer a format and compose against it. Its
    /// internal form is documented nowhere client-facing, on purpose; see Notes/backlog.md item 9
    /// for why that policy was chosen over documenting it.
    /// </summary>
    public class ProxySchemaFilter : ISchemaFilter
    {
        /// <summary>
        /// Applies example values to proxy class schemas
        /// </summary>
        /// <param name="schema">The schema to modify</param>
        /// <param name="context">The schema filter context containing type information</param>
        public void Apply(OpenApiSchema schema, SchemaFilterContext context)
        {
            // RangeFilterProxy examples
            if (context.Type == typeof(RangeFilterProxy))
            {
                schema.Example = new OpenApiObject
                {
                    ["fieldName"] = new OpenApiString("price"),
                    ["lowerLimit"] = new OpenApiDouble(10.0),
                    ["upperLimit"] = new OpenApiDouble(100.0)
                };
            }
            // ValueFilterProxy examples
            else if (context.Type == typeof(ValueFilterProxy))
            {
                schema.Example = new OpenApiObject
                {
                    ["fieldName"] = new OpenApiString("category"),
                    ["value"] = new OpenApiString("electronics")
                };
            }
            // BoostProxy examples
            else if (context.Type == typeof(BoostProxy))
            {
                schema.Example = new OpenApiObject
                {
                    ["boostStrength"] = new OpenApiInteger((int)BoostStrength.Med),
                    ["filterProxy"] = new OpenApiObject
                    {
                        ["hashString"] = new OpenApiString("example-filter-token-12345")
                    }
                };
            }
            // CombinedFilterProxy examples
            else if (context.Type == typeof(CombinedFilterProxy))
            {
                schema.Example = new OpenApiObject
                {
                    ["a"] = new OpenApiObject
                    {
                        ["hashString"] = new OpenApiString("example-filter-token-12345")
                    },
                    ["b"] = new OpenApiObject
                    {
                        ["hashString"] = new OpenApiString("example-filter-token-67890")
                    },
                    ["useAndOperation"] = new OpenApiBoolean(true)
                };
            }
            // FilterProxy examples
            else if (context.Type == typeof(FilterProxy))
            {
                schema.Example = new OpenApiObject
                {
                    ["hashString"] = new OpenApiString("example-filter-token-12345")
                };
            }
        }
    }
}
