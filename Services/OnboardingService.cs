using IndxServer.Data;
using IndxServer.Engine;
using IndxServer.Services.Observr;
using IndxServer.Services.SearchOrAsk;
using Microsoft.EntityFrameworkCore;

namespace IndxServer.Services
{
    /// <summary>One step of Get started: what it is, whether it is done, where it takes you, and
    /// whether it is a Pro feature.</summary>
    public sealed record OnboardingStep(string Key, string Title, string Line, bool Done, string? Href, bool Pro = false);

    public sealed record OnboardingGroup(string Title, IReadOnlyList<OnboardingStep> Steps);

    /// <summary>
    /// Get started as the user sees it on a team: the list (first or invited, as it applies now),
    /// its groups, whether it is hidden, and whether it ends in the Pro teaser (the first user, on
    /// an instance without Observr).
    /// </summary>
    public sealed record OnboardingView(string Kind, Guid TeamId, string TeamName, IReadOnlyList<OnboardingGroup> Groups,
        bool Hidden, bool Teaser, bool TeaserTeam)
    {
        public IEnumerable<OnboardingStep> Steps => Groups.SelectMany(g => g.Steps);
        public int Total => Steps.Count();
        public int Done => Steps.Count(s => s.Done);
        public bool Finished => Done == Total;
        public string? Next => Steps.FirstOrDefault(s => !s.Done)?.Key;
    }

    /// <summary>
    /// Get started (Nov 2026; mockup: indx-react/apps/components, "Get started"; data:
    /// <see cref="Onboarding"/>). Made for an account when it is provisioned: the first user on a
    /// new server or empty team sets search up, someone invited to a team finds their way, by role.
    /// A step is done by what has happened, read here each time: the team has a dataset, a dataset
    /// with records, a search from outside the console, another member, Search or ask or Observr
    /// on. What nothing records (a dataset opened, the preview tried, a statistics page
    /// read, a synonym or boost saved, a question tried) is a mark the page sets as it happens.
    /// Search or ask is a step where the instance shows it, on any plan; Observr is Pro, a step
    /// where the instance has it, and without it the first user's list ends in a teaser for it. Scoped per circuit, so the column's card and
    /// the team page's welcome, two islands, hear the same events. A tour of the console ("Show me
    /// around") was built and removed the same day (2 Nov 2026): not worth the time it took.
    /// </summary>
    internal sealed class OnboardingService(
        ApplicationDbContext db,
        ITeamDatasets datasets,
        StatisticsService statistics,
        SearchOrAskService searchOrAsk,
        SearchOrAskStore searchOrAskStore,
        ObservrSubscription observr,
        ObservrFindingStore findings,
        IEditionService edition,
        InviteService invites,
        Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider auth,
        ILogger<OnboardingService> logger)
    {
        /// <summary>A step was marked, a list hidden or shown: the card redraws.</summary>
        public event Action? Changed;

        /// <summary>Get started under the account menu: the card opens its panel.</summary>
        public event Action? OpenRequested;

        /// <summary>Gives a new account its Get started on the team it landed on. Once per user
        /// and team; a later call leaves the first in place.</summary>
        public async Task StartAsync(string userId, Guid teamId, string kind)
        {
            if (await db.Onboardings.AnyAsync(o => o.UserId == userId && o.TeamId == teamId)) return;
            db.Onboardings.Add(new Onboarding { Id = Guid.NewGuid(), UserId = userId, TeamId = teamId, Kind = kind, CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        private readonly HashSet<string> _marked = new(StringComparer.Ordinal);

        /// <summary>Marks a step done by a visit (open, try, stats, tune, soa-try). Cheap to
        /// call on every search: once marked in this circuit it is not written again.</summary>
        public async Task MarkAsync(string userId, string teamId, string key)
        {
            if (!Guid.TryParse(teamId, out var team) || !_marked.Add($"{team}\0{key}")) return;
            var row = await db.Onboardings.FirstOrDefaultAsync(o => o.UserId == userId && o.TeamId == team);
            if (row == null) return;
            var marks = row.Marks.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
            if (!marks.Add(key)) return;
            row.Marks = string.Join(' ', marks.Order(StringComparer.Ordinal));
            await db.SaveChangesAsync();
            Changed?.Invoke();
        }

        /// <summary>Marks a step for whoever is signed in to this circuit: the pages call this
        /// as the thing happens (a dataset opened, the preview searched).</summary>
        public async Task MarkAsync(string teamId, string key)
        {
            try
            {
                var user = (await auth.GetAuthenticationStateAsync()).User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
                if (user != null) await MarkAsync(user, teamId, key);
            }
            catch (Exception ex)
            {
                // A mark is a nicety: a page never fails for it.
                logger.LogDebug(ex, "Get started could not mark {Step}", key);
            }
        }

        public Task HideAsync(string userId, Guid teamId) => SetHiddenAsync(userId, teamId, DateTime.UtcNow);

        /// <summary>Get started again, from the account menu: shown, made for this team if the
        /// account had none (one from before it shipped, or a team joined later), as the list it fits.</summary>
        public async Task ShowAsync(string userId, Guid teamId, bool teamHasDatasets)
        {
            if (!await db.Onboardings.AnyAsync(o => o.UserId == userId && o.TeamId == teamId))
                await StartAsync(userId, teamId, teamHasDatasets ? Onboarding.KindInvited : Onboarding.KindFirst);
            await SetHiddenAsync(userId, teamId, null);
            // Whether or not anything changed, the card loads it and opens: the menu asked to see it.
            // Until Nov 2026 nothing was raised when the list was already shown, so the item did nothing.
            Changed?.Invoke();
            OpenRequested?.Invoke();
        }

        private async Task SetHiddenAsync(string userId, Guid teamId, DateTime? at)
        {
            var row = await db.Onboardings.FirstOrDefaultAsync(o => o.UserId == userId && o.TeamId == teamId);
            if (row == null || row.HiddenAt == at) return;
            row.HiddenAt = at;
            await db.SaveChangesAsync();
            Changed?.Invoke();
        }

        /// <summary>Get started for the user on the team, or null when the account has none there.</summary>
        public async Task<OnboardingView?> GetAsync(string userId, Team team, string role)
        {
            var row = await db.Onboardings.AsNoTracking().FirstOrDefaultAsync(o => o.UserId == userId && o.TeamId == team.Id);
            if (row == null) return null;
            var teamId = team.Id.ToString();
            var names = datasets.GetTeamDataSets(teamId);
            var marks = row.Marks.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
            // Invited to a team that has nothing yet, as its admin: there is search to set up.
            var first = row.Kind == Onboarding.KindFirst || (names.Count == 0 && TeamRoles.CanAdmin(role));
            var groups = first ? await FirstAsync(team, teamId, names, marks) : Invited(team, names, marks, role);
            return new OnboardingView(first ? Onboarding.KindFirst : Onboarding.KindInvited, team.Id, team.Name, groups,
                row.HiddenAt != null, first && !observr.Visible, !edition.IsEnabled(EditionFeature.MultipleUsers));
        }

        private string Tab(Team team, IReadOnlyList<string> names, string tab) =>
            names.Count == 0 ? TeamRoutes.Team(team.Name) : $"{TeamRoutes.Dataset(team.Name, names[0])}?tab={tab}";

        private async Task<IReadOnlyList<OnboardingGroup>> FirstAsync(Team team, string teamId, List<string> names, HashSet<string> marks)
        {
            var indexed = names.Any(n => IsIndexed(n, teamId));
            var setUp = new List<OnboardingStep>
            {
                new("data", "Add your data", "Upload a JSON file, or start from a demo dataset.", names.Count > 0, TeamRoutes.Team(team.Name)),
                new("fields", "Choose what to search", "Pick the fields people search, filter and sort by.", indexed, Tab(team, names, "fields")),
                new("try", "Try a search", "Type in the preview, typos and all.", marks.Contains("try"), Tab(team, names, "search")),
            };
            var work = new List<OnboardingStep>
            {
                new("connect", "Connect your site", "Make an API key and put the search box on your page.",
                    statistics.Store?.TeamHasSearches(teamId) ?? false, TeamRoutes.TeamKeys(team.Name)),
            };
            if (edition.IsEnabled(EditionFeature.MultipleUsers))
            {
                var members = await db.TeamMembers.CountAsync(m => m.TeamId == team.Id);
                var pending = (await invites.ForTeamAsync(team.Id)).Count;
                work.Add(new("invite", "Invite your team", "Bring in the people who tune the search with you.", members > 1 || pending > 0, TeamRoutes.TeamSettings(team.Name)));
            }
            var groups = new List<OnboardingGroup> { new("Set up your search", setUp), new("Put it to work", work) };
            var pro = new List<OnboardingStep>();
            if (searchOrAsk.Shown)
                pro.Add(new("soa", "Answer questions", "Search or ask writes answers from your records when a visitor asks.",
                    names.Any(n => searchOrAskStore.Load(teamId, n)?.Enabled == true), Tab(team, names, "search-or-ask")));
            if (observr.Visible)
                pro.Add(new("observr", "Let Observr watch", "A weekly look at what visitors could not find, and what to change.",
                    names.Any(n => findings.IsEnabled(teamId, n)), TeamRoutes.TeamObservr(team.Name), Pro: true));
            if (pro.Count > 0) groups.Add(new("Answer and observe", pro));
            return groups;
        }

        private IReadOnlyList<OnboardingGroup> Invited(Team team, List<string> names, HashSet<string> marks, string role)
        {
            var groups = new List<OnboardingGroup>
            {
                new("Find your way", [
                    new("open", "Open a dataset", "Status, statistics and settings, one dataset at a time.", marks.Contains("open"), TeamRoutes.Team(team.Name)),
                    new("try", "Try a search", "See what visitors see, in the preview.", marks.Contains("try"), Tab(team, names, "search")),
                ]),
            };
            if (TeamRoles.CanWrite(role))
            {
                var better = new List<OnboardingStep>
                {
                    new("tune", "Add a synonym or a boost rule", "Teach search the words your visitors use, or lift what should come first.",
                        marks.Contains("tune"), Tab(team, names, "synonyms")),
                };
                if (searchOrAsk.Shown && names.Any(n => searchOrAsk.ShownFor(team.Id.ToString(), n)))
                    better.Add(new("soa-try", "Ask a question", "Try Search or ask on a dataset, as a visitor would.", marks.Contains("soa-try"),
                        Tab(team, names, "search-or-ask")));
                groups.Add(new("Make it better", better));
            }
            else
                groups.Add(new("Follow along", [
                    new("stats", "See what people search for", "The searches, what they found and what was chosen.", marks.Contains("stats"), Tab(team, names, "statistics")),
                ]));
            return groups;
        }

        /// <summary>Records on disk and a field configuration: indexed once, whether awake now or
        /// asleep. Read from the store, so asking never wakes a dataset.</summary>
        private bool IsIndexed(string name, string teamId)
        {
            try
            {
                var (records, fields) = IndxServerInternalApi.Manager.GetStoreFacts(name, teamId);
                return records > 0 && fields > 0;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Get started could not read {Dataset}'s store", name);
                return false;
            }
        }
    }
}
