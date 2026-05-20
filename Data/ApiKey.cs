using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IndxCloudApi.Data
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

        public DateTime CreatedAt { get; set; }
        public DateTime ExpiresAt { get; set; }
        public bool IsRevoked { get; set; }

        [ForeignKey(nameof(UserId))]
        public ApplicationUser? User { get; set; }
    }
#pragma warning restore 1591
}
