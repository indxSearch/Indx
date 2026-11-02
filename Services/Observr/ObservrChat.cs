using System.Text.Json;

namespace IndxServer.Services.Observr
{
    /// <summary>What a question came to: the answer's text, or why there is none, and the finding
    /// that holds its proposals when it made any.</summary>
    public sealed record ObservrChatOutcome(ObservrStatus Status, string Text = "", long FindingId = 0, string? Reason = null);

    /// <summary>What the Agent is doing while it answers. A tool call has its Tool and a Done line
    /// for once it has run ("Searched for \"oak\""); reading the question and writing have neither.</summary>
    public sealed record ObservrChatStatus(string Text, string? Done = null, string? Tool = null);

    /// <summary>
    /// The Agent's loop (Notes/Anders/console-shell-plan-2026-10-13.md, stage 5): a question goes
    /// to Observr with the context, the tools and the conversation so far; Observr answers, or asks
    /// for tools, which run here, and the conversation goes up again until there is an answer. The
    /// turns are kept per conversation, an answer with a short note of what it looked at; a proposal
    /// becomes a finding, so Yes and No go through the same path as every other finding's.
    /// </summary>
    internal sealed class ObservrChat(ObservrClient client, ObservrTools tools, ObservrConversationStore conversations,
        ObservrFindingStore findings, ILogger<ObservrChat> logger)
    {
        public const string Kind = "chat";
        /// <summary>Model turns a question may take before the loop gives up.</summary>
        private const int MaxTurns = 8;
        private const int MaxToolResult = 12_000;
        /// <summary>What an answer keeps of each thing it looked at, and of all of them: enough for a
        /// follow-up to point back ("the second one"), not the whole result again.</summary>
        private const int LookedExcerpt = 600;
        private const int LookedTotal = 4000;
        /// <summary>Longest question, the same number as Observr's AskEndpoint.MaxQuestion: over it
        /// the question is refused here with a reason the panel can word, not with Observr's 400.</summary>
        public const int MaxQuestion = 4000;

        private sealed record Turn(string Role, string? Text = null, List<ObservrChatToolUse>? ToolUses = null, List<ToolResult>? ToolResults = null);
        private sealed record ToolResult(string Id, string Content);

        /// <summary>A user turn as the model reads it: prefixed with where it was asked when that
        /// is not where the owner is now.</summary>
        internal static string Placed(ObservrTurn t, ObservrChatContext ctx)
        {
            if (t.TeamId == ctx.TeamId && t.DatasetName == ctx.DatasetKey) return t.Text;
            if (t.TeamId == ctx.TeamId) return t.DatasetName.Length > 0 ? $"(about the dataset {t.DatasetName}) {t.Text}" : $"(about the team) {t.Text}";
            return t.DatasetName.Length > 0 ? $"(about the dataset {t.DatasetName} of another team) {t.Text}" : "(about another team) " + t.Text;
        }

        /// <summary>An earlier answer as the model reads it again: its text, and what it looked at.</summary>
        internal static string Recalled(ObservrTurn t)
        {
            var looked = t.LookedAt();
            if (looked.Count == 0) return t.Text;
            var sb = new System.Text.StringBuilder(t.Text).Append("\n\n[What this answer looked at]");
            foreach (var l in looked) sb.Append("\n- ").Append(l.Done).Append(": ").Append(l.Result);
            return sb.ToString();
        }

        /// <summary>Asks in a conversation (the user's current one, or a new one, when none is
        /// named), and tells <paramref name="status"/> what is being read along the way.</summary>
        public async Task<ObservrChatOutcome> AskAsync(ObservrChatContext ctx, string userId, string question, Func<ObservrChatStatus, Task> status, CancellationToken ct,
            long? conversationId = null)
        {
            var conversation = conversationId ?? conversations.Current(userId, DateTime.UtcNow) ?? conversations.Next(userId);
            question = question.Trim();
            if (question.Length == 0) return new ObservrChatOutcome(ObservrStatus.Failed, Reason: "empty");
            if (question.Length > MaxQuestion) return new ObservrChatOutcome(ObservrStatus.Failed, Reason: "tooLong");
            if (!client.HasToken) return new ObservrChatOutcome(ObservrStatus.NoToken);

            // The conversation as the panel shows it, from where its memory starts (the same rule
            // draws the panel's line): an earlier turn may be about another dataset or team, and a
            // user turn from elsewhere says where, so the model does not read it as this one. An
            // answer comes with what it looked at. The question itself is kept only once Observr has
            // answered a turn: a refusal (the service down, the licence refused) is shown in the
            // panel and leaves no trace, which the model would otherwise read as conversation.
            var history = conversations.Turns(userId, conversation);
            var from = ObservrConversationStore.RememberedFrom(history);
            // The part past the memory, kept as a summary Observr writes (the earlier summary folded
            // in), made once per turn that falls out; without one the panel's line says it is gone.
            var summary = from > 0 ? await SummaryAsync(ctx, userId, conversation, history, from, status, ct) : null;
            var messages = new List<Turn>();
            foreach (var t in history.Skip(from))
                if (t.Text.Length > 0)
                {
                    var text = t.Role == ObservrTurn.User ? Placed(t, ctx) : Recalled(t);
                    if (messages.Count == 0 && summary != null) text = $"[Earlier in this conversation, summarised]\n{summary}\n\n[The conversation goes on]\n{text}";
                    messages.Add(new Turn(t.Role, text));
                }
            messages.Add(new Turn(ObservrTurn.User, question));
            var looked = new List<ObservrLooked>();
            var kept = false;

            var definitions = tools.Definitions(ctx);
            var context = tools.Describe(ctx);
            var fields = tools.FieldNames(ctx);
            int? askId = null;
            var proposals = new List<ObservrProposal>();
            await status(new ObservrChatStatus("Reading the question"));
            for (var turn = 1; turn <= MaxTurns; turn++)
            {
                var payload = new
                {
                    team = ctx.TeamName, dataset = ctx.DataSetName, askId, question = askId == null ? question : null,
                    context, tools = definitions, fields,
                    messages = messages.Select(m => new { role = m.Role, text = m.Text, toolUses = m.ToolUses, toolResults = m.ToolResults }).ToList(),
                };
                var (st, reply, reason) = await client.ChatTurnAsync(payload, ctx.TeamId, ctx.DatasetKey, ct);
                if (st != ObservrStatus.Ok || reply == null)
                {
                    // Mid-conversation (after a tool turn) the question is already in the thread, so
                    // the refusal closes it; before any answer, nothing is written.
                    if (kept) conversations.Add(userId, ctx.TeamId, ctx.DatasetKey, ObservrTurn.Assistant, ObservrText.Refusal(new ObservrOutcome(st, Reason: reason), "to answer"),
                        conversationId: conversation);
                    return new ObservrChatOutcome(st, Reason: reason);
                }
                if (!kept) { conversations.Add(userId, ctx.TeamId, ctx.DatasetKey, ObservrTurn.User, question, conversationId: conversation); kept = true; }
                askId = reply.AskId;
                proposals.AddRange(reply.Proposals);
                if (reply.ToolUses.Count == 0)
                {
                    var text = reply.Text.Length > 0 ? reply.Text : "(Observr said nothing.)";
                    long findingId = 0;
                    if (proposals.Count > 0)
                        findingId = findings.Add(ctx.TeamId, ctx.DatasetKey, Kind, question,
                            new ObservrAnswer(reply.AskId, "proposal", text, proposals, reply.Model));
                    conversations.Add(userId, ctx.TeamId, ctx.DatasetKey, ObservrTurn.Assistant, text, findingId > 0 ? findingId : null,
                        conversation, looked);
                    return new ObservrChatOutcome(ObservrStatus.Ok, text, findingId);
                }

                messages.Add(new Turn(ObservrTurn.Assistant, reply.Text.Length > 0 ? reply.Text : null, reply.ToolUses));
                var results = new List<ToolResult>();
                foreach (var use in reply.ToolUses)
                {
                    string content;
                    // A tool the service ran itself (propose, docs) comes with its result.
                    if (use.Result != null) content = use.Result;
                    else if (use.Name == "propose")
                        content = reply.Problem ?? "Recorded. Now answer the owner and say what you proposed.";
                    else
                    {
                        var step = Step(use.Name, use.Input);
                        await status(step);
                        content = await Task.Run(() => tools.Run(use.Name, use.Input, ctx), ct);
                        if (content.Length > MaxToolResult) content = content[..MaxToolResult] + "\n(cut)";
                        // Kept with the answer, short, within the answer's total.
                        var room = LookedTotal - looked.Sum(l => l.Result.Length);
                        if (room > 0)
                        {
                            var excerpt = content.Length > Math.Min(LookedExcerpt, room) ? content[..Math.Min(LookedExcerpt, room)] + "…" : content;
                            looked.Add(new ObservrLooked(use.Name, step.Done ?? step.Text, excerpt));
                        }
                    }
                    results.Add(new ToolResult(use.Id, content));
                }
                messages.Add(new Turn(ObservrTurn.User, null, null, results));
                await status(new ObservrChatStatus("Writing"));
            }
            logger.LogInformation("The Agent ran out of turns for {Team}/{Dataset}", ctx.TeamName, ctx.DataSetName);
            conversations.Add(userId, ctx.TeamId, ctx.DatasetKey, ObservrTurn.Assistant, "I read more than I could put together into an answer. Ask it in a smaller piece.",
                conversationId: conversation);
            return new ObservrChatOutcome(ObservrStatus.Failed, Reason: "turns");
        }

        /// <summary>The summary of the turns before <paramref name="from"/>: the kept one while it covers
        /// them all, else a new one written by Observr from the kept one and the turns since. Null
        /// when none could be had.</summary>
        private async Task<string?> SummaryAsync(ObservrChatContext ctx, string userId, long conversation, List<ObservrTurn> history, int from,
            Func<ObservrChatStatus, Task> status, CancellationToken ct)
        {
            var kept = conversations.Summary(userId, conversation);
            var lastOut = history[from - 1].Id;
            if (kept is { } k && k.UpToId >= lastOut) return k.Text;
            var fallen = history.Take(from).Where(t => kept == null || t.Id > kept.Value.UpToId).Where(t => t.Text.Length > 0).ToList();
            if (fallen.Count == 0) return kept?.Text;
            await status(new ObservrChatStatus("Remembering the earlier part"));
            try
            {
                if (await client.SummarizeAsync(ctx.TeamId, ctx.TeamName, kept?.Text, fallen, ct) is { } written)
                {
                    conversations.SetSummary(userId, conversation, lastOut, written);
                    return written;
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested) { logger.LogWarning(ex, "The Agent could not summarise a long conversation"); }
            return kept?.Text;
        }

        /// <summary>A tool call as the owner reads it: while it runs, and once it has.</summary>
        private static ObservrChatStatus Step(string name, Dictionary<string, JsonElement> input)
        {
            string Arg(string key) => input.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            var (running, done) = name switch
            {
                "search" => ($"Searching for \"{Arg("text")}\"", $"Searched for \"{Arg("text")}\""),
                "statistics" => ("Reading the statistics", "Read the statistics"),
                "query" => ("Looking up the query", "Looked up the query"),
                "fields" => ("Reading the fields and a sample of the records", "Read the fields and a sample of the records"),
                "synonyms" => ("Reading the synonyms", "Read the synonyms"),
                "settings" => ("Reading the settings", "Read the settings"),
                "findings" => ("Reading what Observr found earlier", "Read what Observr found earlier"),
                "datasets" => ("Reading the team's datasets", "Read the team's datasets"),
                "zero_hit_evidence" => ($"Looking at what \"{Arg("query")}\" nearly finds", $"Looked at what \"{Arg("query")}\" nearly finds"),
                "docs" => ($"Reading up on {Arg("topic").Replace('-', ' ')}", $"Read up on {Arg("topic").Replace('-', ' ')}"),
                _ => ("Reading", "Read"),
            };
            return new ObservrChatStatus(running, done, name);
        }
    }
}
