using IndxServer.Data;
using Microsoft.AspNetCore.Identity;

namespace IndxServer.Services.Support
{
    /// <summary>
    /// Fetches Indx's replies: every few minutes while a ticket is open (nothing is open, nothing
    /// is asked; writing one starts it again), the list from the Portal. With every list, its own or
    /// one a page asked for (SupportState.OnRefreshed), each ticket with an unread reply has its
    /// newest reply told once through the notification bell and email (NotificationType.SupportReply):
    /// to the person who wrote the ticket when they have an account here, else to the instance's
    /// admins. The Portal is never told the reply was seen by this; only a person opening the thread
    /// does that.
    /// </summary>
    internal sealed class SupportPollJob : BackgroundService
    {
        private readonly SupportState state;
        private readonly SupportClient client;
        private readonly SupportOptions options;
        private readonly IServiceScopeFactory scopes;
        private readonly ILogger<SupportPollJob> logger;
        // Two lists at once (the check and a page) must not tell the same reply twice.
        private readonly SemaphoreSlim _telling = new(1, 1);

        public SupportPollJob(SupportState state, SupportClient client, SupportOptions options, IServiceScopeFactory scopes, ILogger<SupportPollJob> logger)
        {
            this.state = state; this.client = client; this.options = options; this.scopes = scopes; this.logger = logger;
            state.OnRefreshed = TellNewRepliesAsync;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!options.Enabled) return;
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); // after startup and the licence fetch
            var every = TimeSpan.FromMinutes(Math.Clamp(options.PollMinutes, 1, 60));
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (client.Token() != null && state.AnyOpen) await CheckAsync(stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogError(ex, "Support: checking for replies failed");
                }
                await Task.Delay(every, stoppingToken);
            }
        }

        /// <summary>Asks the Portal for the list; the replies in it are told by <see cref="TellNewRepliesAsync"/>.</summary>
        internal Task CheckAsync(CancellationToken ct = default) => state.RefreshAsync(ct);

        private async Task TellNewRepliesAsync(SupportList list)
        {
            if (!list.Tickets.Any(t => t.Unread)) return;
            await _telling.WaitAsync();
            try
            {
                foreach (var summary in list.Tickets.Where(t => t.Unread))
                {
                    var thread = await client.GetAsync(summary.Id, seen: false);
                    if (thread.Value is not { } ticket) continue;
                    var reply = ticket.Messages.LastOrDefault(m => m.FromStaff);
                    if (reply is null) continue;
                    var writer = ticket.Messages.FirstOrDefault(m => !m.FromStaff)?.AuthorEmail;
                    await TellAsync(ticket, reply, writer);
                }
            }
            catch (Exception ex)
            {
                // A page asked for the list; telling must not fail it.
                logger.LogError(ex, "Support: telling a reply failed");
            }
            finally { _telling.Release(); }
        }

        private async Task TellAsync(SupportTicket ticket, SupportMessage reply, string? writerEmail)
        {
            using var scope = scopes.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var notifications = scope.ServiceProvider.GetRequiredService<NotificationService>();
            var writer = writerEmail is { Length: > 0 } ? await users.FindByEmailAsync(writerEmail) : null;
            var recipients = writer is not null ? [writer] : await users.GetUsersInRoleAsync("Admin");
            var source = $"support:{ticket.Id}:{reply.Id}";
            var title = $"Indx answered: {ticket.Subject}";
            var body = reply.Body.Length > 1500 ? reply.Body[..1500] + "…" : reply.Body;
            foreach (var user in recipients)
            {
                if (await notifications.ExistsAsync(user.Id, NotificationType.SupportReply, source)) continue;
                await notifications.CreateAsync(user.Id, NotificationType.SupportReply, title,
                    $"{reply.AuthorName}: {body}\n\nRead and reply under Support in the console.",
                    metadata: $"/support/{ticket.Id}", sourceId: source);
            }
        }
    }
}
