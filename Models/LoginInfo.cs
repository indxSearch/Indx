namespace IndxServer.Models
{
    /// <summary>
    /// Class to support JSON serialization of user credentials
    /// </summary>
    public class LoginInfo
    {
        #region Public Properties
#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member
        public string UserEmail { get; set; } = "";
        public string UserPassWord { get; set; } = "";
#pragma warning restore CS1591 // Missing XML comment for publicly visible type or member
        #endregion Public Properties
    }
}