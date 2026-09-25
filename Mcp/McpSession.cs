using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using IndxServer.Services;

namespace IndxServer.Mcp
{
    /// <summary>
    /// Shapes each MCP session to the key that opened it: the tools it lists and the instructions
    /// it hands over.
    ///
    /// <para>Both used to be fixed. The server told every client to call <c>describe_dataset</c>
    /// before searching and advertised every tool, while a Search key — the level you issue for a
    /// search integration — can do neither. The mandated workflow was impossible for the most
    /// ordinary key there is, and the only way to find out was to call a tool and be refused, once
    /// per tool. See <c>Notes/mcp-first-contact-2026-09-25.md</c>.</para>
    ///
    /// <para>Everything here is read-only. There is no MCP tool that changes anything, which is
    /// what makes "an MCP key cannot alter your data or your configuration" a sentence worth
    /// saying — see <see cref="Reaches"/>.</para>
    /// </summary>
    internal static class McpSession
    {
        /// <summary>
        /// Called once per session, at the initialize handshake, with the HTTP context that opened
        /// it — so the key's claims decide what this session sees.
        /// </summary>
        internal static Task ConfigureAsync(HttpContext http, McpServerOptions options, CancellationToken _)
        {
            options.ServerInstructions = InstructionsFor(ApiKeyScope.For(http)?.Level);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Hides from <c>tools/list</c> the tools this key cannot call.
        ///
        /// <para>A listing filter rather than a smaller tool collection, deliberately. Removing
        /// the tool from the session would make the dispatcher answer "Unknown tool", and the
        /// refusal it would otherwise give — "This API key is limited to Search only; this tool
        /// needs at least Read only" — is the most useful sentence in the whole surface. Filtering
        /// only the list keeps the advertised set honest and the refusal intact for a client that
        /// calls a tool it was not offered.</para>
        ///
        /// <para>Registered once at startup, not per session: it reads the level from the request
        /// each time, so there is no per-session state to accumulate.</para>
        /// </summary>
        internal static readonly McpRequestFilter<ListToolsRequestParams, ListToolsResult> FilterListedTools =
            next => async (context, cancellationToken) =>
            {
                var result = await next(context, cancellationToken);
                var level = ApiKeyScope.For(
                    context.Services?.GetService<IHttpContextAccessor>()?.HttpContext)?.Level;
                result.Tools = [.. result.Tools.Where(t => Reaches(level, t.Name))];
                return result;
            };

        /// <summary>
        /// Whether a key of this level can actually call the tool. Mirrors the levels the tools
        /// themselves require; an unscoped key (a login token, or one issued before scopes existed)
        /// is limited by team role only, so it sees everything.
        /// </summary>
        internal static bool Reaches(ApiKeyLevel? level, string toolName) => level switch
        {
            null => true,
            ApiKeyLevel.Search => toolName is "list_datasets" or "search" or "get_document",
            _ => true,
        };

        /// <summary>
        /// What the server says about itself at the handshake. Two variants, because with no write
        /// tool there are only two postures: a key that can search, and a key that can also look.
        /// </summary>
        internal static string InstructionsFor(ApiKeyLevel? level)
            => level == ApiKeyLevel.Search ? SearchOnly : ReadAndSearch;

        private const string Shared = """
            Matching is precise by default. Indx matches on character patterns rather than by
            tokenising and stemming, so it handles typos, inflections and compound words without
            configuration, but this server disables fuzzy pattern matches so that an empty result
            is a trustworthy "not here" rather than noise. If a query returns nothing and you
            believe the data is there, retry once with broaden=true before concluding anything.

            Scores are relative within one result set: use them to rank, not as a percentage, and
            do not compare them across queries. The dataset owner may have configured boost rules,
            which apply automatically, so ranking reflects their intent rather than text similarity
            alone.

            Nothing here changes anything. Every tool is read-only, so configuration is changed by
            a person in the web console or over the HTTP API, never from this connection.
            """;

        /// <summary>A Search key: list and search, and that is all it can reach.</summary>
        private static readonly string SearchOnly = $"""
            Indx is a search engine over the datasets this server holds. Datasets belong to teams,
            so every call takes a team name and a dataset name; list_datasets gives you the pairs
            you can reach.

            Normal order: list_datasets to find a dataset, then search it.

            This connection uses a Search key, so the tools that describe a dataset's fields are
            not available to you. That means you cannot see which fields are filterable or what
            values they hold, so do not attempt filters: search on text alone, and say so if asked
            to filter. A key with Read access would let you inspect fields; ask whoever issued
            this one.

            {Shared}
            """;

        /// <summary>Read or better: the full workflow, describe_dataset included.</summary>
        private static readonly string ReadAndSearch = $"""
            Indx is a search engine over the datasets this server holds. Datasets belong to teams,
            so every call takes a team name and a dataset name; list_datasets gives you the pairs
            you can reach.

            Normal order: list_datasets to find a dataset, describe_dataset to learn its fields and
            the real values they hold, then search. Do not guess field names or filter values:
            describe_dataset reports the distinct values of facetable fields and the ranges of
            numeric ones, and a filter on a field that is not filterable is refused.

            {Shared}
            """;
    }
}
