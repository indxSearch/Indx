using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IndxServer.Data
{
#pragma warning disable 1591
    /// <summary>
    /// Join row: a user's membership in a team, carrying their role on that team's datasets.
    /// Composite primary key (TeamId, UserId) gives exactly one membership row per user/team.
    /// </summary>
    public class TeamMember
    {
        public Guid TeamId { get; set; }

        [Required]
        public string UserId { get; set; } = "";

        /// <summary>"Admin" | "Editor" | "Viewer" — role on every dataset owned by the team.</summary>
        [Required, MaxLength(20)]
        public string Role { get; set; } = "";

        public DateTime CreatedAt { get; set; }

        [ForeignKey(nameof(TeamId))]
        public Team? Team { get; set; }

        [ForeignKey(nameof(UserId))]
        public ApplicationUser? User { get; set; }
    }
#pragma warning restore 1591
}
