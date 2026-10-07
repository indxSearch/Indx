using Indx.Http;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace IndxServer.Swagger
{
    /// <summary>
    /// Adds multiple named examples for the Search endpoint
    /// </summary>
    public class SearchExamplesOperationFilter : IOperationFilter
    {
        /// <summary>
        /// Applies multiple named examples to the Search endpoint operation
        /// </summary>
        /// <param name="operation">The OpenAPI operation to modify</param>
        /// <param name="context">The operation filter context</param>
        public void Apply(OpenApiOperation operation, OperationFilterContext context)
        {
            // Only apply to the Search endpoint (POST api/Search/{dataSetName})
            if (context.ApiDescription.HttpMethod != "POST" ||
                !context.ApiDescription.RelativePath?.StartsWith("api/Search/") == true)
                return;

            // Find the QueryProxy parameter in the request body
            var requestBody = operation.RequestBody;
            if (requestBody?.Content == null)
                return;

            foreach (var content in requestBody.Content.Values)
            {
                content.Examples = new Dictionary<string, OpenApiExample>
                {
                    ["Simple"] = new OpenApiExample
                    {
                        Summary = "Simple Search",
                        Description = "Basic search with only required parameters",
                        Value = new OpenApiObject
                        {
                            ["text"] = new OpenApiString("string"),
                            ["maxNumberOfRecordsToReturn"] = new OpenApiInteger(30)
                        }
                    },
                    ["Full"] = new OpenApiExample
                    {
                        Summary = "Full Search",
                        Description = "A search with the commonly set parameters. Coverage values left out take the dataset's query parameters",
                        Value = new OpenApiObject
                        {
                            ["text"] = new OpenApiString("string"),
                            ["maxNumberOfRecordsToReturn"] = new OpenApiInteger(30),
                            ["enableCoverage"] = new OpenApiBoolean(true),
                            // Coverage values left out come from the dataset's query parameters
                            // (GET query-parameters). Each one sent here wins over the dataset's,
                            // so send only what this search needs to decide for itself.
                            ["coverageSetup"] = new OpenApiObject
                            {
                                ["truncate"] = new OpenApiBoolean(true)
                            },
                            ["enableFacets"] = new OpenApiBoolean(false),
                            ["enableBoost"] = new OpenApiBoolean(false),
                            ["removeDuplicates"] = new OpenApiBoolean(true),
                            ["sortAscending"] = new OpenApiBoolean(false),
                            ["timeOutLimitMilliseconds"] = new OpenApiInteger(1000),
                            ["sortBy"] = new OpenApiString(""),
                            ["logPrefix"] = new OpenApiString(""),
                            ["filter"] = new OpenApiNull(),
                            ["boosts"] = new OpenApiNull()
                        }
                    }
                };
            }
        }
    }
}
