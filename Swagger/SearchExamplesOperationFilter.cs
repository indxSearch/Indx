using Indx.CloudApi;
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

            // Find the CloudQuery parameter in the request body
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
                        Description = "Complete search with all parameters and default values",
                        Value = new OpenApiObject
                        {
                            ["text"] = new OpenApiString("string"),
                            ["maxNumberOfRecordsToReturn"] = new OpenApiInteger(30),
                            ["enableCoverage"] = new OpenApiBoolean(true),
                            ["coverageDepth"] = new OpenApiInteger(1000),
                            ["coverageSetup"] = new OpenApiObject
                            {
                                ["levenshteinMaxWordSize"] = new OpenApiInteger(20),
                                ["minWordSize"] = new OpenApiInteger(2),
                                ["truncateWordHitLimit"] = new OpenApiInteger(1),
                                ["truncateWordHitTolerance"] = new OpenApiInteger(0),
                                ["coverWholeQuery"] = new OpenApiBoolean(true),
                                ["coverWholeWords"] = new OpenApiBoolean(true),
                                ["coverFuzzyWords"] = new OpenApiBoolean(true),
                                ["coverJoinedWords"] = new OpenApiBoolean(true),
                                ["coverPrefixSuffix"] = new OpenApiBoolean(true),
                                ["truncate"] = new OpenApiBoolean(true),
                                ["includePatternMatches"] = new OpenApiBoolean(true),
                                ["truncationScore"] = new OpenApiInteger(255)
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
