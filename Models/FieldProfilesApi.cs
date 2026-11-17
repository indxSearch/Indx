using IndxServer.Services;

namespace IndxServer.Models
{
    /// <summary>A profile as GET fields/profiles shows it: its values, whether it is one of the
    /// five built-ins, and whether this dataset changed that built-in.</summary>
    public sealed record FieldProfileView(string Id, string Name, string About, bool BuiltIn, bool Edited, Indx.Api.Tolerance Tolerance,
        float RerankWeight, int[] IndexSizes, int NumberOfStartCharacters, int NumberOfStopCharacters, IReadOnlyList<string> Fields,
        int? TypingPrefixFrom = null, Indx.Api.TextRule[]? TextRules = null)
    {
        public static FieldProfileView Of(FieldProfile p, DatasetFieldProfiles own, IReadOnlyList<string> fields) =>
            new(p.Id, p.Name, p.About, FieldProfiles.IsBuiltInId(p.Id), FieldProfiles.IsEditedBuiltIn(p.Id, own), p.Tolerance,
                p.RerankWeight, p.IndexSizes, p.NumberOfStartCharacters, p.NumberOfStopCharacters, fields, p.TypingPrefixFrom, p.TextRules);
    }

    /// <summary>GET fields/profiles: every profile, which field has which, the searched fields with
    /// settings of their own (no profile), and the profile a newly searched field gets.</summary>
    public sealed record FieldProfilesResponse(IReadOnlyList<FieldProfileView> Profiles, IReadOnlyDictionary<string, string> Assignments,
        IReadOnlyList<string> Custom, string DefaultProfileId);

    /// <summary>
    /// PUT fields/profiles. Each part left out (null) is left as it is. <c>Profiles</c> is the
    /// dataset's own list, whole: a profile with a built-in's id is that built-in as this dataset
    /// changes it, one left out of the list is deleted (a built-in back to its shipped values).
    /// <c>Assignments</c> gives fields their profile by id, or null for none (the field keeps its
    /// values, as its own); fields not named keep theirs. <c>DefaultProfileId</c> is the profile a
    /// field gets when it becomes searched.
    /// </summary>
    public sealed record FieldProfilesRequest(List<FieldProfile>? Profiles, Dictionary<string, string?>? Assignments, string? DefaultProfileId);
}
