using Microsoft.Extensions.Caching.Memory;

namespace IndxCloudApi.Services
{
    /// <summary>
    /// JWT validation caches "is this token's jti revoked?" for a few minutes so every request
    /// does not hit the database. Whoever revokes a key must evict that entry, otherwise the
    /// key keeps authenticating until the entry expires.
    /// </summary>
    internal static class ApiKeyRevocation
    {
        public static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

        public static string CacheKey(string jti) => $"jti_revoked_{jti}";

        /// <summary>Call right after persisting <c>IsRevoked = true</c> so the next request re-checks the database.</summary>
        public static void Evict(IMemoryCache cache, string jti) => cache.Remove(CacheKey(jti));
    }
}
