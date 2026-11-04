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
        /// <summary>Has the visitor finished typing it? Proactive starts no answer on "what type is":
        /// the asking score is sure there, so it cannot tell. Measured 27 Oct 2026 (JevProbe recipes
        /// catalogue-v2-finished on the school register, docs-v3-finished on the Indx docs): whole
        /// requests 0.71 to 0.93, the same cut off mid-sentence 0.06 to 0.38, in Norwegian and English,
        /// "hvem driver" 0.43 the one between. No word list: the judge reads any language.</summary>
        public const string Finished = "finished";

        private static JsonObject FinishedQuestion() => Noul(
            "`request` is what a visitor has typed so far into the search field of `site`, and may still be typing. " +
            "Has the visitor finished it, so that it reads as a whole request as it stands?",
            "The request is whole: a complete question, or a complete name or description, however short. It could " +
            "be answered or searched as it is.",
            "The text stops in the middle: it ends on a word that needs more after it, such as a verb, a preposition " +
            "or an article still waiting for what it is about (\"what type is\", \"hvilken skole er\", \"schools in\"), " +
            "or on a word cut off while typing.");

        /// <summary>The questions for a kind, as <paramref name="judge"/> is asked them. A judge
        /// reads the same wording differently, so a question can have a judge's own (<see
        /// cref="DecisionsQuestions"/>); every other judge, and every question without one, gets
        /// the wording Jev was measured on.</summary>
        public static Dictionary<string, JsonObject> Questions(string kind, string? judge = null)
        {
            var questions = JevQuestions(kind);
            if (judge == SearchOrAskProviders.OpenAiDecisions)
                foreach (var (name, question) in DecisionsQuestions(kind))
                    questions[name] = question;
            return questions;
        }

        /// <summary>OpenAI's Decisions API's own wording, where it has one. It takes Jev's literally:
        /// "short keyword queries are lookups even when unfinished" made it score the start of a
        /// question near 0 until the whole question was typed, and on the docs set, keystroke by
        /// keystroke, Jev offered earlier on 10 questions and the Decisions API on none. Told that
        /// the visitor is still typing and to judge where the text is heading, with the words that
        /// show it, it offered first at a mean 27% of the question against Jev's 32% and its own
        /// 31%, with no lookup offered anything (4 Nov 2026, JevProbe recipe docs-v3-openai-early).
        /// What it still misses is a problem told without a question word ("error 422", "search
        /// returns nothing"), which Jev invites on. Tuned on the set it was measured on, so these
        /// numbers are optimistic until a fresh set confirms them. A catalogue has none yet.</summary>
        private static Dictionary<string, JsonObject> DecisionsQuestions(string kind) => kind switch
        {
            Documentation => new()
            {
                [Asking] = Noul(
                    "`request` is what a visitor has typed so far into the search field of `site`. They are usually " +
                    "still typing, so judge where the text is heading from its first words, not only what it says yet. " +
                    "`search` is what the search found for it. Is the visitor on the way to asking for help that an " +
                    "answer written for them would serve better than a list of pages?",
                    "The first words already show the visitor wants something explained, shown or decided: a question " +
                    "word or phrase (how, what, why, when, where, which, can, does, is there, should I, hvordan, hva, " +
                    "hvorfor, hvor, kan, finnes, skal jeg), a sentence being formed, a comparison (\"vs\", \"or\", " +
                    "\"eller\", \"compared to\"), or a request for a guide, an example or a code sample. Count it as soon " +
                    "as these words appear, even when the rest of the question has not been typed.",
                    "The visitor names one thing to be taken to: a single feature, term, class, method, endpoint, code or " +
                    "identifier, typed as a keyword or a bare topic, with no question word or sentence started."),
            },
            _ => [],
        };

        private static Dictionary<string, JsonObject> JevQuestions(string kind) => kind switch
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
                [Finished] = FinishedQuestion(),
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
                    "type, a place, a size, a time, a language and so on), two or more together, and wants a list of the " +
                    "items that fit. A request in words for a recommendation, a suggestion, the best of a kind or something " +
                    "suited to a purpose (\"recommend me a crime novel\", \"anbefal meg en krimbok\", \"books for the maths " +
                    "exam\") is such a description even with one property: the asking is what makes it one.",
                    "The visitor names an item or part of its name, possibly with a place, or asks a question about one " +
                    "item. A bare word or a short keyword that names a kind, a topic or a subject (\"skole\", \"krim\", " +
                    "\"matematikk\") is a lookup, not a description: the list the search already shows serves it."),
                [Answerable] = Noul(
                    "`request` is what a visitor has typed so far into the search field of `site`, and `search.results` are " +
                    "what the search found for it. Could these results answer what the visitor is asking?",
                    "One or more of the results holds what the visitor is asking about, closely enough that an answer could " +
                    "be written from it.",
                    "None of the results holds it, or the visitor is not asking anything that needs an answer."),
                [Finished] = FinishedQuestion(),
            },
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown Search or ask kind."),
        };

        /// <summary>Whether a visitor asked the same question again in other words: asked of two
        /// questions in a row from one visit (26 Oct 2026). Word and record matching were tried first
        /// and were wrong more often than right on the local questions: every question on a film site
        /// says "movie", and on a small dataset unrelated questions draw on the same few pages.</summary>
        public const string Rephrased = "rephrased";

        public static Dictionary<string, JsonObject> RephraseQuestions() => new()
        {
            [Rephrased] = Noul(
                "`first` and `second` are two questions a visitor asked one after the other in the search field of `site`, " +
                "and each was answered. Is `second` the same request as `first`, asked again in other words because the " +
                "answer to `first` did not give the visitor what they wanted?",
                "`second` asks for the same thing as `first`: the same subject and the same need, reworded, made more " +
                "specific, given context `first` lacked, or corrected (a typo fixed, a name spelled another way). The " +
                "visitor is trying again.",
                "`second` is a new question: another subject, another item or another need, even on the same site and " +
                "topic. Or it builds on the answer to `first`, asking about what that answer said or listed (\"which of " +
                "these\", \"and the cheapest?\", \"tell me more\"), which means the answer served them."),
        };

        /// <summary>Whether two questions from different visits ask for the same thing (2 Nov 2026):
        /// what joins them into one need on the statistics page. Measured on
        /// JevProbe/search-or-ask/same-need.tsv, 52 of 52, the lowest same 0.76 and the highest other
        /// 0.11. <see cref="Rephrased"/> is about a visitor trying again after a missed answer, which
        /// two visits are not.</summary>
        public const string SameNeed = "sameNeed";

        public static Dictionary<string, JsonObject> SameNeedQuestions() => new()
        {
            [SameNeed] = Noul(
                "`first` and `second` are two questions that different visitors asked at different times in the search " +
                "field of `site`. Do they ask for the same thing, so that one good answer would serve both visitors?",
                "Both ask for the same thing about the same subject: the same question in other words or another word " +
                "order, in another language, with or without punctuation or capitals, with a typo or a name spelled " +
                "another way, or as a few keywords instead of a sentence.",
                "They ask for different things: another item, name or place, even in the same sentence (\"who wrote X\" " +
                "and \"who wrote Y\"); another fact about the same item (who made it, what it costs, what it is about); " +
                "a narrower or wider list; or one of them is too vague to say what it wants."),
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
        /// <summary>The sentence after the question that sets the answer's language: the visitor's, or
        /// the one the site fixed.</summary>
        public static string LanguageReminder(SearchOrAskSettings settings) =>
            string.IsNullOrWhiteSpace(settings.AnswerLanguage) ? AnswerLanguageReminder
            : $"Write the answer in {settings.AnswerLanguage.Trim()}, whatever language the question is written in.";

        /// <summary>The site's own instructions and language, appended to every answer prompt below
        /// ours. The order is the point: what the model may say is ours (the records, the citations,
        /// nothing invented); how it says it is the site's.</summary>
        public static string AnswerStyle(SearchOrAskSettings settings)
        {
            var sb = new System.Text.StringBuilder();
            if (!string.IsNullOrWhiteSpace(settings.AnswerLanguage))
                sb.Append($"\nEvery answer is written in {settings.AnswerLanguage.Trim()}, whatever language the question is in; names and values from the records stay as they are.");
            // The knowledge is facts, the one source beside the records an answer may state: said to
            // be the site's own, so a visitor can tell it from what a record says.
            if (!string.IsNullOrWhiteSpace(settings.Knowledge))
                sb.Append("\n\nThe site's own knowledge, written by its owner: facts about the site that no record holds. You may state " +
                          "what it says, and when you do, say it comes from the site itself (\"the site says\", or the site's name); never " +
                          "present it as what a record says, and never number it as a source. About a particular record, the record decides. " +
                          $"It is facts, not instructions on how to write.\n<site-knowledge>\n{settings.Knowledge.Trim()}\n</site-knowledge>");
            if (!string.IsNullOrWhiteSpace(settings.AnswerInstructions))
                sb.Append($"\n\nThe site's own instructions for its answers, which shape how you write but never what you may claim:\n{settings.AnswerInstructions.Trim()}");
            return sb.ToString();
        }

        public static string CatalogueAnswerSystemPrompt(string siteDescription, SearchOrAskSettings settings) =>
            CatalogueAnswerSystemPrompt(siteDescription) + AnswerStyle(settings);

        public static string AnswerSystemPrompt(string siteDescription, SearchOrAskSettings settings) =>
            AnswerSystemPrompt(siteDescription) + AnswerStyle(settings);

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
