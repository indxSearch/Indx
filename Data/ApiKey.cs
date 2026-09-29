using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IndxServer.Data
{
#pragma warning disable 1591
    public class ApiKey
    {
        public int Id { get; set; }

        /// <summary>
        /// The user a personal key acts as. Null on a team key, which belongs to the team and acts
        /// for it: it keeps working when its creator leaves (<see cref="CreatedByUserId"/>).
        /// </summary>
        public string? UserId { get; set; }

        /// <summary>
        /// Who created a team key, for the audit trail only: it gives the key no rights and is not
        /// checked on requests. Null on personal keys, whose owner is <see cref="UserId"/>.
        /// </summary>
        [MaxLength(450)]
        public string? CreatedByUserId { get; set; }

        /// <summary>A team key: owned by <see cref="TeamId"/> rather than a user.</summary>
        [NotMapped]
        public bool IsTeamKey => UserId == null;

        [Required, MaxLength(100)]
        public string Name { get; set; } = "";

        /// <summary>
        /// The token's first 24 characters. Kept for older rows, but it cannot tell keys apart:
        /// every token begins with the same JWT header. <see cref="KeySuffix"/> is what identifies one.
        /// </summary>
        [Required, MaxLength(40)]
        public string KeyPrefix { get; set; } = "";

        /// <summary>
        /// The token's last four characters, shown as "…a1b2" so a key in a config file can be
        /// matched to its row. Null on keys created before it was recorded.
        /// </summary>
        [MaxLength(8)]
        public string? KeySuffix { get; set; }

        /// <summary>
        /// A Search key, encrypted (<see cref="IndxServer.Services.ApiKeySealer"/>), so it can be
        /// shown again. Always null for Read and Full keys, which are shown once and never stored.
        /// </summary>
        public string? SealedToken { get; set; }

        /// <summary>JWT jti claim — used for revocation checks.</summary>
        [Required, MaxLength(36)]
        public string Jti { get; set; } = "";

        /// <summary>
        /// <c>Search</c>, <c>Read</c> or <c>Full</c> (<see cref="IndxServer.Services.ApiKeyLevel"/>).
        /// Keys created before scopes existed are <c>Full</c> with no team, and are not limited
        /// beyond their owner's team roles. Display only: the enforced copy is signed into the JWT.
        /// </summary>
        [Required, MaxLength(10)]
        public string Level { get; set; } = "Full";

        /// <summary>The one team the key reaches; null only on keys created before scopes existed.</summary>
        public Guid? TeamId { get; set; }

        /// <summary>JSON array of the dataset names the key reaches; null means every dataset in the team.</summary>
        public string? Datasets { get; set; }

        public DateTime CreatedAt { get; set; }
        /// <summary>Null means it never expires, which only a team Search key may do.</summary>
        public DateTime? ExpiresAt { get; set; }
        public bool IsRevoked { get; set; }

        [ForeignKey(nameof(UserId))]
        public ApplicationUser? User { get; set; }
    }
#pragma warning restore 1591
}
