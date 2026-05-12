using Microsoft.AspNetCore.Identity;

namespace IndxCloudApi.Data
{
    /// <summary>
    /// Application user entity extending ASP.NET Core Identity user.
    /// Add custom profile properties here as needed.
    /// </summary>
    public class ApplicationUser : IdentityUser
    {
        /// <summary>
        /// When true the user must change their password before they can call any
        /// protected API or UI endpoint other than the password-change flow itself.
        /// Set on the seeded admin so first-login forces a rotation away from the
        /// deployment-time initial password.
        /// </summary>
        public bool MustChangePassword { get; set; }
    }
}
