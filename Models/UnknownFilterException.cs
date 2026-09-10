using System;

namespace IndxCloudApi.Models
{
    /// <summary>
    /// Thrown when a request references a filter token that the engine cannot resolve —
    /// the token is malformed, names a field that no longer exists or is no longer
    /// Filterable, or carries bounds that do not parse. The controller maps this to
    /// HTTP 400 with error code <c>unknownFilter</c>.
    /// <para>
    /// The alternative — treating an unresolvable token as "no filter" — is why this
    /// type exists: a search whose filter silently evaporates answers 200 with the
    /// whole corpus, which is the opposite of what the caller asked for and impossible
    /// to detect from the response.
    /// </para>
    /// </summary>
    public sealed class UnknownFilterException : Exception
    {
        /// <summary>Creates the exception, naming the token that could not be resolved.</summary>
        public UnknownFilterException(string hashString)
            : base($"The filter '{Describe(hashString)}' could not be resolved. " +
                   "Create the filter first and reference it by the returned hashString; " +
                   "a filter also stops resolving if its field was removed or is no longer Filterable.")
        { }

        /// <summary>
        /// Renders the token for a message body. Derived-filter keys use '\0' and '\u0001'
        /// as brackets, so they are stripped rather than echoed into the response, and a
        /// long key is truncated — the caller is being told which token failed, not handed
        /// its own request back verbatim.
        /// </summary>
        private static string Describe(string? hashString)
        {
            if (string.IsNullOrEmpty(hashString))
                return "(empty)";
            var cleaned = hashString.Replace('\0', ' ').Replace('\u0001', ' ').Trim();
            return cleaned.Length <= 120 ? cleaned : cleaned.Substring(0, 117) + "...";
        }
    }
}
