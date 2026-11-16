using System.Security.Claims;

namespace IndxServer.Services
{
    /// <summary>
    /// Whether the address an external provider gave is one the provider vouches for. An account
    /// here is found by its address, so an address that is only claimed must never find one: a
    /// provider that lets its directory's owner write any address into a user (the Microsoft
    /// account handler reads <c>mail</c> from whatever tenant the user is in) would otherwise sign
    /// that user in as the account with that address. Google says so in <c>email_verified</c>,
    /// mapped in <c>StartupServices</c>; a provider that says nothing has vouched for nothing.
    /// </summary>
    public static class ExternalEmail
    {
        /// <summary>The claim a provider's verified flag is mapped to.</summary>
        public const string VerifiedClaim = "email_verified";

        /// <summary>True only when the provider said the address is verified.</summary>
        public static bool IsVerified(ClaimsPrincipal principal) =>
            string.Equals(principal.FindFirstValue(VerifiedClaim), "true", StringComparison.OrdinalIgnoreCase);
    }
}
