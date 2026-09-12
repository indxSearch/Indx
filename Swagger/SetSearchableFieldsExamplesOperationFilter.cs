using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace IndxServer.Swagger
{
    /// <summary>
    /// Adds examples for the SetSearchableFields endpoint
    /// </summary>
    public class SetSearchableFieldsExamplesOperationFilter : IOperationFilter
    {
        /// <summary>
        /// Applies examples to the SetSearchableFields endpoint operation
        /// </summary>
        /// <param name="operation">The OpenAPI operation to modify</param>
        /// <param name="context">The operation filter context</param>
        public void Apply(OpenApiOperation operation, OperationFilterContext context)
        {
            // Only apply to the SetSearchableFields endpoint (PUT api/SetSearchableFields/{dataSetName})
            if (context.ApiDescription.HttpMethod != "PUT" ||
                !context.ApiDescription.RelativePath?.StartsWith("api/SetSearchableFields/") == true)
                return;

            // Find the request body
            var requestBody = operation.RequestBody;
            if (requestBody?.Content == null)
                return;

            foreach (var content in requestBody.Content.Values)
            {
                content.Examples = new Dictionary<string, OpenApiExample>
                {
                    ["Single Field"] = new OpenApiExample
                    {
                        Summary = "Single Field",
                        Description = "Set a single searchable field with weight (0=High, 1=Med, 2=Low)",
                        Value = new OpenApiArray
                        {
                            new OpenApiObject
                            {
                                ["Item1"] = new OpenApiString("title"),
                                ["Item2"] = new OpenApiInteger(0)
                            }
                        }
                    },
                    ["Full"] = new OpenApiExample
                    {
                        Summary = "Multiple Fields",
                        Description = "Set multiple searchable fields with varying weights (0=High, 1=Med, 2=Low)",
                        Value = new OpenApiArray
                        {
                            new OpenApiObject
                            {
                                ["Item1"] = new OpenApiString("title"),
                                ["Item2"] = new OpenApiInteger(0)
                            },
                            new OpenApiObject
                            {
                                ["Item1"] = new OpenApiString("description"),
                                ["Item2"] = new OpenApiInteger(1)
                            },
                            new OpenApiObject
                            {
                                ["Item1"] = new OpenApiString("content"),
                                ["Item2"] = new OpenApiInteger(2)
                            },
                            new OpenApiObject
                            {
                                ["Item1"] = new OpenApiString("tags"),
                                ["Item2"] = new OpenApiInteger(0)
                            }
                        }
                    }
                };
            }
        }
    }
}
