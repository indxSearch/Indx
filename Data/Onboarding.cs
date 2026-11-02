using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IndxServer.Data
{
#pragma warning disable 1591
    /// <summary>
    /// Get started for one user on one team (Nov 2026; mockup: indx-react/apps/components, "Get
    /// started"). Made when the account is, so accounts from before it see nothing: the first user
    /// on a new server or empty team gets the list that sets search up (<see cref="KindFirst"/>),
    /// someone invited to a team the list that finds their way (<see cref="KindInvited"/>). Most
    /// steps are read from what exists (a dataset, a key, a search from a site); the ones nothing
    /// else records are <see cref="Marks"/>: the tour, a dataset opened, the preview tried.
    /// </summary>
    public class Onboarding
    {
        public const string KindFirst = "first";
        public const string KindInvited = "invited";

        public Guid Id { get; set; }

        [Required, MaxLength(450)]
        public string UserId { get; set; } = "";

        public Guid TeamId { get; set; }

        [Required, MaxLength(20)]
        public string Kind { get; set; } = KindFirst;

        /// <summary>The steps marked done by a visit, space-separated keys.</summary>
        [Required, MaxLength(400)]
        public string Marks { get; set; } = "";

        public DateTime CreatedAt { get; set; }

        /// <summary>Set by Hide; Get started under the account menu clears it.</summary>
        public DateTime? HiddenAt { get; set; }

        [ForeignKey(nameof(TeamId))]
        public Team? Team { get; set; }
    }
#pragma warning restore 1591
}
