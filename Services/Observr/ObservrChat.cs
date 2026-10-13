using System.Text.Json;

namespace IndxServer.Services.Observr
{
    /// <summary>What a question came to: the answer's text, or why there is none, and the finding
    /// that holds its proposals when it made any.</summary>
    public sealed record ObservrChatOutcome(ObservrStatus Status, string Text = "", long FindingId = 0, string? Reason = null);

    /// <summary>
    /// The Agent's loop (Notes/Anders/console-shell-plan-2026-10-13.md, stage 5): a question goes
    /// to Observr with the context, the tools and the conversation so far; Observr answers, or asks
    /// for tools, which run here, and the conversation goes up again until there is an answer. The
    /// text turns are kept per user and context; a proposal becomes a finding, so Yes and No go
    /// through the same path as every other finding's.
    /// </summary>
    internal sealed class ObservrChat(ObservrClient client, ObservrTools tools, ObservrConversationStore conversations,
        ObservrFindingStore findings, ILogger<ObservrChat> logger)
    {
        public const string Kind = "chat";
        /// <summary>Model turns a question may take before the loop gives up.</summary>
        private const int MaxTurns = 8;
        /// <summary>Earlier text turns the model sees, from the kept conversation.</summary>
        private const int HistoryTurns = 10;
        private const int MaxToolResult = 12_000;

        private sealed record Turn(string Role, string? Text = null, List<ObservrChatToolUse>? ToolUses = null, List<ToolResult>? ToolResults = null);
        private sealed record ToolResult(string Id, string Content);

        /// <summary>Asks, and tells <paramref name="status"/> what is being read along the way.</summary>
        public async Task<ObservrChatOutcome> AskAsync(ObservrChatContext ctx, string userId, string question, Func<string, Task> status, CancellationToken ct)
        {
            question = question.Trim();
            if (question.Length == 0) return new ObservrChatOutcome(ObservrStatus.Failed, Reason: "empty");
            if (!client.HasToken) return new ObservrChatOutcome(ObservrStatus.NoToken);
            conversations.Add(userId, ctx.TeamId, ctx.DatasetKey, ObservrTurn.User, question);

            var history = conversations.List(userId, ctx.TeamId, ctx.DatasetKey, HistoryTurns + 1);
            var messages = new List<Turn>();
            foreach (var t in history.Take(history.Count - 1))
                if (t.Text.Length > 0) messages.Add(new Turn(t.Role, t.Text));
            messages.Add(new Turn(ObservrTurn.User, question));

            var definitions = tools.Definitions(ctx);
            var context = tools.Describe(ctx);
            var fields = tools.FieldNames(ctx);
            int? askId = null;
            var proposals = new List<ObservrProposal>();
            await status("Reading the question");
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
                    conversations.Add(userId, ctx.TeamId, ctx.DatasetKey, ObservrTurn.Assistant, ObservrText.Refusal(new ObservrOutcome(st, Reason: reason), "to answer"));
                    return new ObservrChatOutcome(st, Reason: reason);
                }
                askId = reply.AskId;
                proposals.AddRange(reply.Proposals);
                if (reply.ToolUses.Count == 0)
                {
                    var text = reply.Text.Length > 0 ? reply.Text : "(Observr said nothing.)";
                    long findingId = 0;
                    if (proposals.Count > 0)
                        findingId = findings.Add(ctx.TeamId, ctx.DatasetKey, Kind, question,
                            new ObservrAnswer(reply.AskId, "proposal", text, proposals, reply.Model));
                    conversations.Add(userId, ctx.TeamId, ctx.DatasetKey, ObservrTurn.Assistant, text, findingId > 0 ? findingId : null);
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
                        await status(Step(use.Name, use.Input));
                        content = await Task.Run(() => tools.Run(use.Name, use.Input, ctx), ct);
                        if (content.Length > MaxToolResult) content = content[..MaxToolResult] + "\n(cut)";
                    }
                    results.Add(new ToolResult(use.Id, content));
                }
                messages.Add(new Turn(ObservrTurn.User, null, null, results));
                await status("Writing");
            }
            logger.LogInformation("The Agent ran out of turns for {Team}/{Dataset}", ctx.TeamName, ctx.DataSetName);
            conversations.Add(userId, ctx.TeamId, ctx.DatasetKey, ObservrTurn.Assistant, "I read more than I could put together into an answer. Ask it in a smaller piece.");
            return new ObservrChatOutcome(ObservrStatus.Failed, Reason: "turns");
        }

        /// <summary>A tool call as the owner reads it while waiting.</summary>
        private static string Step(string name, Dictionary<string, JsonElement> input)
        {
            string Arg(string key) => input.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            return name switch
            {
                "search" => $"Searching for \"{Arg("text")}\"",
                "statistics" => "Reading the statistics",
                "fields" => "Reading the fields and a sample of the records",
                "synonyms" => "Reading the synonyms",
                "settings" => "Reading the settings",
                "findings" => "Reading what Observr found earlier",
                "datasets" => "Reading the team's datasets",
                "zero_hit_evidence" => $"Looking at what \"{Arg("query")}\" nearly finds",
                "docs" => $"Reading up on {Arg("topic").Replace('-', ' ')}",
                _ => "Reading",
            };
        }
    }
}
