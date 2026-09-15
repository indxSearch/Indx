using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IndxServer.Data
{
#pragma warning disable 1591
    public class ApiKey
    {
        public int Id { get; set; }

        [Required]
        public string UserId { get; set; } = "";

        [Required, MaxLength(100)]
        public string Name { get; set; } = "";

        [Required, MaxLength(40)]
        public string KeyPrefix { get; set; } = "";

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
        public DateTime ExpiresAt { get; set; }
        public bool IsRevoked { get; set; }

        [ForeignKey(nameof(UserId))]
        public ApplicationUser? User { get; set; }
    }
#pragma warning restore 1591
}
