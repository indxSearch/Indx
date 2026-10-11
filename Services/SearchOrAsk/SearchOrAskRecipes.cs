using System.Text.Json.Nodes;

namespace IndxServer.Services.SearchOrAsk
{
    /// <summary>
    /// The wording the judge is asked with, per kind of dataset. Jev is not trained: this text is
    /// all it knows about the task, so it is the product, and each kind is the wording a replay
    /// measured (JevProbe/search-or-ask/recipes). Change one only with a replay behind it.
    /// </summary>
    public static class SearchOrAskRecipes
    {
        /// <summary>Documentation, help and reference: docs-v2, measured on the Indx docs (34 of 36
        /// questions answered, none of 20 lookups offered anything).</summary>
        public const string Documentation = "documentation";

        public static readonly string[] Kinds = [Documentation];

        public static bool IsKnown(string? kind) => kind != null && Kinds.Contains(kind);

        /// <summary>The question whose probability drives the offer.</summary>
        public const string Asking = "asking";
        public const string Answerable = "answerable";

        public static Dictionary<string, JsonObject> Questions(string kind) => kind switch
        {
            Documentation => new()
            {
                [Asking] = Noul(
                    "`request` is what a visitor has typed so far into the search field of `site`, possibly unfinished. " +
                    "`search` is what the search found for it. Is the visitor asking for help that an answer written for " +
                    "them would serve better than a list of pages?",
                    "The visitor wants something explained, shown or decided for them: how to do something, why something " +
                    "happens, whether something is possible, the difference between two or more things (\"vs\", \"or\", " +
                    "\"eller\", \"compared to\"), which option to choose, or an example, a code sample or a guide for a task " +
                    "they describe. Unfinished text counts when its start already shows this, such as a question word or a " +
                    "sentence being formed.",
                    "The visitor names one thing to be taken to: a single feature, term, class, method, endpoint, code or " +
                    "identifier, typed as a keyword or a bare topic. Short keyword queries are lookups even when vague or " +
                    "unfinished, unless they compare things or ask for an example."),
                [Answerable] = Noul(
                    "`request` is what a visitor has typed so far into the search field of `site`, and `search.results` are " +
                    "the pages the search found for it. Could these pages answer what the visitor is asking?",
                    "One or more of the results covers what the visitor is asking about, closely enough that an answer could " +
                    "be written from them.",
                    "None of the results covers it, or the visitor is not asking anything that needs an answer."),
            },
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown Search or ask kind."),
        };

        /// <summary>A yes/no question answered with a probability ("noul" in Jev's API).</summary>
        private static JsonObject Noul(string instructions, string whenTrue, string whenFalse) => new()
        {
            ["type"] = "noul",
            ["instructions"] = instructions,
            ["criteria"] = new JsonObject { ["true"] = whenTrue, ["false"] = whenFalse },
        };

        /// <summary>The answer model's instructions. It may use the numbered sources and nothing
        /// else: an answer from its own knowledge is the one failure this feature cannot have.</summary>
        public static string AnswerSystemPrompt(string siteDescription) =>
            $"""
            You answer a visitor's question on a website. About the site: {siteDescription}

            Answer only from the numbered sources given with the question. They are records from the site's
            own search. Cite them with their number in square brackets, like [2], after the words they support.
            If the sources do not hold the answer, say so plainly in one sentence and name the closest source,
            if any. Never answer from your own knowledge, and never invent a name, a value, a setting or a step
            the sources do not state.

            Write in the language the question is written in. The description of the site says who its readers
            are; it does not choose the language of an answer, and neither do the sources.
            Lead with the answer and keep it short. Markdown is rendered. Do not restate the question, and do
            not close with an offer of more help or a request for more details.
            """;
    }
}
