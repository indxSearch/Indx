using System.ComponentModel.DataAnnotations;

namespace IndxCloudApi.Data
{
#pragma warning disable 1591
    /// <summary>
    /// A team owns datasets. The team <see cref="Name"/> is the single, globally-unique,
    /// human-facing label — it doubles as the URL segment, so there is no separate slug or
    /// display name. The surrogate <see cref="Id"/> is the stable internal key everything
    /// (incl. the soft <c>DataSet.TeamId</c> column in indx.db) points at, so a rename never
    /// rewrites references.
    /// </summary>
    public class Team
    {
        public Guid Id { get; set; }

        /// <summary>Globally unique, URL-safe (a-z, 0-9, '-'). The only name a team has.</summary>
        [Required, MaxLength(50)]
        public string Name { get; set; } = "";

        public DateTime CreatedAt { get; set; }

        public ICollection<TeamMember> Members { get; set; } = new List<TeamMember>();
    }
#pragma warning restore 1591
}
