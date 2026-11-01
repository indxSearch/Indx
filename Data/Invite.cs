using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IndxServer.Data
{
#pragma warning disable 1591
    /// <summary>
    /// An invitation to the instance, and with <see cref="TeamId"/> to a team on it. The address
    /// may register in any registration mode while the invite lasts; with a team it joins that
    /// team in <see cref="Role"/> instead of getting a team of its own. Single-use: removed once
    /// the account exists. Until Nov 2026 an invite was an address on a list in settings.json,
    /// in Invite mode only, with no team (Services/InviteService.cs).
    /// </summary>
    public class Invite
    {
        public Guid Id { get; set; }

        [Required, MaxLength(256)]
        public string Email { get; set; } = "";

        /// <summary>Upper-case, as Identity normalises emails, for lookups.</summary>
        [Required, MaxLength(256)]
        public string NormalizedEmail { get; set; } = "";

        /// <summary>The team the address joins; null for an invite to the instance only.</summary>
        public Guid? TeamId { get; set; }

        /// <summary>"Admin" | "Editor" | "Viewer" on <see cref="TeamId"/>; null without a team.</summary>
        [MaxLength(20)]
        public string? Role { get; set; }

        /// <summary>Who sent it, for the pending list; kept as a plain id, so deleting that user
        /// leaves the invite standing.</summary>
        [MaxLength(450)]
        public string? InvitedByUserId { get; set; }

        public DateTime CreatedAt { get; set; }

        public DateTime ExpiresAt { get; set; }

        /// <summary>SHA-256 of the link's token, hex. The token itself is only in the email.</summary>
        [Required, MaxLength(64)]
        public string TokenHash { get; set; } = "";

        [ForeignKey(nameof(TeamId))]
        public Team? Team { get; set; }
    }
#pragma warning restore 1591
}
