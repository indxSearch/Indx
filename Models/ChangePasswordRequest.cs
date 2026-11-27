namespace IndxServer.Models
{
    /// <summary>
    /// Body of POST /api/changePassword. Used to rotate the seeded admin's
    /// initial password (set at deployment time) before any other API calls
    /// can be made.
    /// </summary>
    public class ChangePasswordRequest
    {
        #region Public Properties
#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member
        public string CurrentPassword { get; set; } = "";
        public string NewPassword { get; set; } = "";
#pragma warning restore CS1591
        #endregion Public Properties
    }
}
