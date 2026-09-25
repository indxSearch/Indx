using System.Security.Claims;
using System.Text.Json;

namespace IndxServer.Services
{
    /// <summary>
    /// What a scoped API key may do. Ordered: a higher level includes everything below it.
    /// A key never exceeds its owner's current role in the team — the role check still runs.
    /// </summary>
    public enum ApiKeyLevel
    {
        /// <summary>
        /// What a search front-end needs and nothing more: search (text, vector, hybrid), document
        /// lookup, building filters, the field lists and the field configuration a filter panel
        /// reads (the types decide value filter or range), and dataset status. Safe to ship in a
        /// browser: a copied key can search the datasets it names, and that is all.
        /// </summary>
        Search = 1,

        /// <summary>Every read: adds export, synonyms, boost rules, counts, dataset lists.</summary>
        Read = 2,

        /// <summary>Everything the owner's team role allows, including writes and deletes.</summary>
        Full = 3,
    }

    /// <summary>
    /// The limits a scoped API key carries, read from its signed token. Set when the key is
    /// created and never changed afterwards (a different scope means a new key), so they live in
    /// the JWT itself rather than being looked up per request.
    /// <para>
    /// A principal without <see cref="LevelClaim"/> is not scoped: a login token, a console cookie
    /// session, or an API key created before scopes existed. Those behave exactly as before —
    /// limited by the user's team roles only.
    /// </para>
    /// </summary>
    public sealed record ApiKeyScope(ApiKeyLevel Level, Guid TeamId, IReadOnlySet<string>? Datasets)
    {
        public const string LevelClaim = "indx_key_level";
        public const string TeamClaim = "indx_key_team";
        /// <summary>JSON array of dataset names; absent means every dataset in the team.</summary>
        public const string DatasetsClaim = "indx_key_datasets";

        private static readonly object ItemsKey = new();

        /// <summary>
        /// <see cref="FromPrincipal"/> for the current request. An unscoped principal — every login
        /// token and console session — costs one claim lookup and allocates nothing. A scoped key's
        /// claims are parsed once and kept on the request, since the filter, the team resolver and
        /// the list endpoints all ask.
        /// </summary>
        public static ApiKeyScope? For(HttpContext? http)
        {
            if (http?.User.FindFirst(LevelClaim) == null) return null;
            if (http.Items.TryGetValue(ItemsKey, out var cached) && cached is ApiKeyScope known)
                return known;
            var scope = FromPrincipal(http.User);
            http.Items[ItemsKey] = scope;
            return scope;
        }

        /// <summary>
        /// The scope carried by <paramref name="principal"/>, or null when it carries none.
        /// A token that carries the level claim but a malformed team or dataset claim is rejected
        /// as <see cref="Invalid"/> — a scoped key must never silently become an unscoped one.
        /// </summary>
        public static ApiKeyScope? FromPrincipal(ClaimsPrincipal? principal)
        {
            var level = principal?.FindFirst(LevelClaim)?.Value;
            if (level == null) return null;
            if (!Enum.TryParse<ApiKeyLevel>(level, ignoreCase: false, out var parsedLevel) || !Enum.IsDefined(parsedLevel))
                return Invalid;
            if (!Guid.TryParse(principal!.FindFirst(TeamClaim)?.Value, out var teamId) || teamId == Guid.Empty)
                return Invalid;

            HashSet<string>? datasets = null;
            var datasetsJson = principal.FindFirst(DatasetsClaim)?.Value;
            if (datasetsJson != null)
            {
                try
                {
                    var names = JsonSerializer.Deserialize<string[]>(datasetsJson);
                    if (names == null || names.Length == 0) return Invalid;
                    datasets = new HashSet<string>(names, StringComparer.Ordinal);
                }
                catch (JsonException)
                {
                    return Invalid;
                }
            }
            return new ApiKeyScope(parsedLevel, teamId, datasets);
        }

        /// <summary>
        /// Stand-in for a scoped token whose claims do not parse: bound to no team, so every
        /// team-scoped check fails and every team-agnostic endpoint refuses it.
        /// </summary>
        public static readonly ApiKeyScope Invalid = new(ApiKeyLevel.Search, Guid.Empty, new HashSet<string>());

        public bool AllowsLevel(ApiKeyLevel required) => TeamId != Guid.Empty && Level >= required;
        public bool AllowsTeam(Guid teamId) => TeamId != Guid.Empty && TeamId == teamId;
        public bool AllowsDataset(string dataSetName) => TeamId != Guid.Empty && (Datasets == null || Datasets.Contains(dataSetName));

        /// <summary>The claims to sign into a new key's JWT.</summary>
        public static IEnumerable<Claim> ClaimsFor(ApiKeyLevel level, Guid teamId, IReadOnlyCollection<string>? datasets)
        {
            yield return new Claim(LevelClaim, level.ToString());
            yield return new Claim(TeamClaim, teamId.ToString());
            if (datasets is { Count: > 0 })
                yield return new Claim(DatasetsClaim, JsonSerializer.Serialize(datasets.Distinct(StringComparer.Ordinal).ToArray()));
        }

        public static string Describe(ApiKeyLevel level) => level switch
        {
            ApiKeyLevel.Search => "Search only",
            ApiKeyLevel.Read => "Read only",
            _ => "Full access",
        };
    }

    /// <summary>
    /// The lowest API key level an action accepts. Actions without it require
    /// <see cref="ApiKeyLevel.Full"/> — fail-closed, so a new endpoint is never reachable by a
    /// browser key by accident.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false)]
    public sealed class KeyAccessAttribute(ApiKeyLevel minimum) : Attribute
    {
        public ApiKeyLevel Minimum { get; } = minimum;

        /// <summary>
        /// Set on an action that is not under <c>teams/{teamName}</c> but narrows its own output to
        /// the key's team and datasets (e.g. <c>me/datasets</c>). Without it, a scoped key is refused
        /// by every action that has no team in its route — so it cannot, for instance, mint a login
        /// token that escapes its team.
        /// </summary>
        public bool FiltersToKeyScope { get; init; }
    }
}
