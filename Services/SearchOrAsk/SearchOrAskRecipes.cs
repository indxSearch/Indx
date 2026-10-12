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

        /// <summary>A catalogue with good fields: catalogue-v1, measured on the school register (40 of
        /// 40 descriptions recognised as criteria, none of 80 others; 36 of 40 questions). A
        /// description of what to list becomes filters (<see cref="SearchOrAskService.FiltersAsync"/>).</summary>
        public const string Catalogue = "catalogue";

        public static readonly string[] Kinds = [Documentation, Catalogue];

        public static bool IsKnown(string? kind) => kind != null && Kinds.Contains(kind);

        /// <summary>The question whose probability drives the offer.</summary>
        public const string Asking = "asking";
        public const string Answerable = "answerable";
        /// <summary>A catalogue's second question: is the visitor describing what to list?</summary>
        public const string Criteria = "criteria";

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
            Catalogue => new()
            {
                [Asking] = Noul(
                    "`request` is what a visitor has typed so far into the search field of `site`, possibly unfinished. " +
                    "`search` is what the search found for it. Is the visitor asking a question about one particular item " +
                    "that a short written answer would serve better than a list?",
                    "The visitor asks something about one particular item: a fact about it, whether it has some property, " +
                    "how much or how many, or which item it is from a description of it. Unfinished text counts when its " +
                    "start already shows this, such as a question word or a sentence being formed. A yes/no question about " +
                    "one named item counts even when it mentions a property others share.",
                    "The visitor names the item they want to be taken to, or describes what kind of items to list, and a " +
                    "list serves them."),
                [Criteria] = Noul(
                    "`request` is what a visitor has typed so far into the search field of `site`, possibly unfinished. Is " +
                    "the visitor describing what kind of items they want listed, rather than naming one item or asking " +
                    "about one?",
                    "The visitor describes items by their properties, the kinds of things `site` says its records hold (a " +
                    "type, a place, a size, a time, a language and so on), usually two or more together, and wants a list " +
                    "of the items that fit.",
                    "The visitor names an item or part of its name, possibly with a place, or asks a question about one item."),
                [Answerable] = Noul(
                    "`request` is what a visitor has typed so far into the search field of `site`, and `search.results` are " +
                    "what the search found for it. Could these results answer what the visitor is asking?",
                    "One or more of the results holds what the visitor is asking about, closely enough that an answer could " +
                    "be written from it.",
                    "None of the results holds it, or the visitor is not asking anything that needs an answer."),
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
        /// <summary>Appended after the question. The second sentence is from a pokédex (12 Oct 2026): "what
        /// pokemon is numer 40" was answered in Spanish, the typo read as a word of another language.</summary>
        public const string AnswerLanguageReminder = "Write the answer in the language the question is written in. " +
            "Judge the language by the whole question, not by one word: a misspelled word is still that language. " +
            "When it is genuinely unclear, write in the language of the site description.";

        /// <summary>A catalogue's answer to a description: a summary of what the search found with the
        /// filters it asked for. From the given facts only, as every answer is.</summary>
        public static string CatalogueAnswerSystemPrompt(string siteDescription) =>
            $"""
            You answer a visitor on a website who described what kind of items they want listed. About the site: {siteDescription}

            The site's search has turned the description into filters and found the items. You get what it
            searched with, how many it found, a summary of everything found, and the first items numbered.
            Tell the visitor what was found, as a short answer they can act on:
            - Lead with how many were found, in the visitor's own words for what they asked for.
            - Then what stands out: what most share, the largest and smallest or other extremes, notable
              differences. Name a few items, citing them with their number in square brackets, like [2].
            - If many were found, end with one short sentence on how to narrow it down, using what the summary
              shows varies (a place, a size, a type).
            Use only the facts given; never add a fact about an item, a count or a place that is not there.
            Field names are internal: say "private", "pupils", "upper secondary", not ErPrivatskole or Elevtall.
            Keep it to a few sentences or a short list. Markdown is rendered. Do not restate the question, and do
            not close with an offer of more help.
            """;

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
