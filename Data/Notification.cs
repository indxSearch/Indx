using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IndxCloudApi.Data
{
#pragma warning disable 1591
    public enum NotificationType
    {
        DatasetShared       = 1,
        AccessRevoked       = 2,
        UserRegistered      = 3,
        UserConfirmedEmail  = 4,
        ApiKeyExpiringSoon  = 5,
        ApiKeyExpired       = 6,
    }

    public class Notification
    {
        public int Id { get; set; }

        [Required]
        public string UserId { get; set; } = "";

        public NotificationType Type { get; set; }

        [Required, MaxLength(200)]
        public string Title { get; set; } = "";

        [Required]
        public string Body { get; set; } = "";

        /// <summary>Optional JSON payload — dataset name, API key ID, link target etc.</summary>
        public string? Metadata { get; set; }

        /// <summary>Stable identifier for background-job deduplication (e.g. ApiKey.Id as string).</summary>
        [MaxLength(100)]
        public string? SourceId { get; set; }

        public bool IsRead { get; set; }

        public DateTime CreatedAt { get; set; }

        [ForeignKey(nameof(UserId))]
        public ApplicationUser? User { get; set; }
    }
#pragma warning restore 1591
}
