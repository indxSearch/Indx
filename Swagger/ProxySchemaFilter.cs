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
    /// The hashString examples are REAL tokens, not placeholders. They used to read
    /// "example-hash-key-12345", which told readers the value was an opaque handle when it is the
    /// filter expression in plain text - so nobody could tell from the docs that a token may be
    /// composed client-side, which is the only way to reach NOT. See the Filters page.
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
                        ["hashString"] = new OpenApiString("VF;category;electronics")
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
                        ["hashString"] = new OpenApiString("VF;category;electronics")
                    },
                    ["b"] = new OpenApiObject
                    {
                        ["hashString"] = new OpenApiString("RF;price;10;100;")
                    },
                    ["useAndOperation"] = new OpenApiBoolean(true)
                };
            }
            // FilterProxy examples
            else if (context.Type == typeof(FilterProxy))
            {
                schema.Example = new OpenApiObject
                {
                    ["hashString"] = new OpenApiString("VF;category;electronics")
                };
            }
        }
    }
}
