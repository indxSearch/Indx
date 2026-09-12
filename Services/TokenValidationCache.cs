using Microsoft.Extensions.Caching.Memory;

namespace IndxServer.Services
{
    /// <summary>
    /// JWT validation (Program.cs OnTokenValidated) caches two per-request lookups for a few
    /// minutes so every call does not hit the database: the user's current security stamp
    /// (null when the user no longer exists) and whether a token's jti has been revoked.
    /// Anything that changes those facts — revoking an API key, changing or resetting a
    /// password — must evict here, otherwise the old verdict stands until the entry expires.
    /// </summary>
    internal static class TokenValidationCache
    {
        public static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

        /// <summary>Claim carried by login tokens (not named API keys): the user's Identity
        /// security stamp at issue time. A mismatch on validation means the password was
        /// changed or reset after the token was minted.</summary>
        public const string SecurityStampClaim = "sstamp";

        public static string JtiKey(string jti) => $"jti_revoked_{jti}";
        public static string UserKey(string userId) => $"user_stamp_{userId}";

        /// <summary>Call right after persisting <c>IsRevoked = true</c> on an API key.</summary>
        public static void EvictJti(IMemoryCache cache, string jti) => cache.Remove(JtiKey(jti));

        /// <summary>Call right after a password change or reset so outstanding login tokens
        /// stop validating now rather than when the cache entry expires.</summary>
        public static void EvictUser(IMemoryCache cache, string userId) => cache.Remove(UserKey(userId));
    }
}
