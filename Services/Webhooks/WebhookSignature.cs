using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace IndxServer.Services.Webhooks
{
    /// <summary>
    /// The signature a webhook call carries: <c>X-Indx-Signature: t=&lt;unix seconds&gt;,sha256=&lt;hex&gt;</c>,
    /// an HMAC-SHA256 with the webhook's secret over <c>"&lt;t&gt;.&lt;body&gt;"</c>. The time is signed
    /// with the body, so a call more than five minutes old is refused: a captured request cannot be
    /// sent again later. The same scheme as Stripe's, so senders already know how to make one.
    /// </summary>
    public static class WebhookSignature
    {
        public const string Header = "X-Indx-Signature";
        public static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);

        /// <summary>The header value for a body sent now (for senders, docs and tests).</summary>
        public static string Sign(string secret, string body, DateTimeOffset at)
        {
            var t = at.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            return $"t={t},sha256={Hex(secret, t, body)}";
        }

        /// <summary>Whether a signature's time, in milliseconds, is within the tolerance of now. On
        /// the numbers: a time no date can hold is a time too far off, not an exception.</summary>
        public static bool WithinTolerance(long nowMs, long sentMs) =>
            sentMs >= nowMs - (long)Tolerance.TotalMilliseconds && sentMs <= nowMs + (long)Tolerance.TotalMilliseconds;

        /// <summary>
        /// What can be said of a signature before the body is read: null when the header is there,
        /// well formed and of now, else why not. A sync's body may be hundreds of megabytes, and is
        /// not read for a call that carries no signature to check it against.
        /// </summary>
        public static string? CheckHeader(string? header, DateTimeOffset now)
        {
            if (string.IsNullOrWhiteSpace(header)) return $"No {Header} header.";
            string? t = null, sig = null;
            foreach (var part in header.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (part.StartsWith("t=", StringComparison.Ordinal)) t = part[2..];
                else if (part.StartsWith("sha256=", StringComparison.Ordinal)) sig = part[7..];
            }
            if (t == null || sig is not { Length: 64 } || !long.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
                return $"{Header} must read t=<unix seconds>,sha256=<hex>.";
            return WithinTolerance(now.ToUnixTimeMilliseconds(), seconds > long.MaxValue / 1000 ? long.MaxValue : seconds * 1000)
                ? null : "The signature's time is more than five minutes from now: sign each call when it is sent.";
        }

        /// <summary>Null when the signature is good, else why not, for the 401 and the log.</summary>
        public static string? Check(string secret, string? header, string body, DateTimeOffset now)
        {
            if (string.IsNullOrWhiteSpace(header)) return $"No {Header} header.";
            string? t = null, sig = null;
            foreach (var part in header.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (part.StartsWith("t=", StringComparison.Ordinal)) t = part[2..];
                else if (part.StartsWith("sha256=", StringComparison.Ordinal)) sig = part[7..];
            }
            if (t == null || sig == null || !long.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
                return $"{Header} must read t=<unix seconds>,sha256=<hex>.";
            if (!WithinTolerance(now.ToUnixTimeMilliseconds(), seconds > long.MaxValue / 1000 ? long.MaxValue : seconds * 1000))
                return "The signature's time is more than five minutes from now: sign each call when it is sent.";
            var expected = Encoding.ASCII.GetBytes(Hex(secret, t, body));
            var given = Encoding.ASCII.GetBytes(sig.ToLowerInvariant());
            return CryptographicOperations.FixedTimeEquals(expected, given) ? null : "The signature does not match the body and the webhook's secret.";
        }

        private static string Hex(string secret, string t, string body)
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
            return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{t}.{body}"))).ToLowerInvariant();
        }
    }
}
