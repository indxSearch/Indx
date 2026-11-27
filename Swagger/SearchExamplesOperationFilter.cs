using System.Text.Json;
using System.Text.Json.Nodes;
using Indx.Api;
using Indx.Http;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace IndxServer.Swagger
{
    /// <summary>
    /// Adds two named examples to the Search endpoint: the smallest request that searches, and one
    /// with every property at its default.
    ///
    /// <para>The full example is <em>serialized from</em> <see cref="QueryProxy"/> and its parts
    /// (<see cref="MatchSetup"/>, <see cref="CandidateSetup"/>, <see cref="RankingProxy"/>) rather
    /// than written out. Until Oct 2026 it was a hand-written
    /// object, and it had drifted: a coverage depth of 1000 where the default is 200, a
    /// <c>coverFuzzyWords</c> that no longer existed, a truncation score of 255 on a 16-bit scale.
    /// Nobody saw, because the filter matched <c>api/Search/</c>, the legacy route hidden from the
    /// document since the route modernization of Aug 2026, and so applied to nothing. An example
    /// that is the defaults by construction cannot drift.</para>
    /// </summary>
    public class SearchExamplesOperationFilter : IOperationFilter
    {
        /// <summary>A search with every setting at the value a dataset with no query parameters
        /// runs with (6.0: the mode and the members of match, candidates and ranking).</summary>
        private static QueryProxy FullExample()
        {
            var e = IndxServer.Services.QueryParameterResolution.Effective(null);
            return new QueryProxy
            {
                Text = "string", MaxNumberOfRecordsToReturn = 30,
                Mode = e.Mode, Match = e.Match, Candidates = e.Candidates, Ranking = e.Ranking,
            };
        }

        /// <summary>
        /// Applies the named examples to the Search endpoint operation
        /// </summary>
        /// <param name="operation">The OpenAPI operation to modify</param>
        /// <param name="context">The operation filter context</param>
        public void Apply(OpenApiOperation operation, OperationFilterContext context)
        {
            // Only the text search: POST .../datasets/{dataSetName}/search (not /search/vector or
            // /search/hybrid, whose bodies are other proxies).
            if (context.ApiDescription.HttpMethod != "POST" ||
                context.ApiDescription.RelativePath?.EndsWith("/search", StringComparison.Ordinal) != true)
                return;

            var content = operation.RequestBody?.Content;
            if (content == null)
                return;

            var full = JsonSerializer.SerializeToNode(
                FullExample(),
                JsonSerializerOptions.Web);

            foreach (var mediaType in content.Values)
            {
                mediaType.Examples = new Dictionary<string, IOpenApiExample>
                {
                    ["Simple"] = new OpenApiExample
                    {
                        Summary = "Simple Search",
                        Description = "Basic search with only required parameters",
                        Value = new JsonObject
                        {
                            ["text"] = "string",
                            ["maxNumberOfRecordsToReturn"] = 30
                        }
                    },
                    ["Full"] = new OpenApiExample
                    {
                        Summary = "Full Search",
                        Description = "Every parameter at its default value, the match, candidate and ranking settings spelled out",
                        Value = full?.DeepClone()
                    }
                };
            }
        }
    }
}
