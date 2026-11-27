using System.Security.Cryptography;
using System.Text;

namespace IndxServer.Services
{
    /// <summary>
    /// Encrypts a Search or Read key so it can be shown again after the one-time reveal. Search
    /// keys are public by design, sitting in a website's page, so hiding them protected nothing and
    /// made people rotate keys they had merely lost. A Read key is shown only to its owner or the
    /// team's Admins, who could create a new one anyway. Full keys change and delete data and are
    /// never stored in any form that gives them back (<see cref="ApiKeyService.CanBeShownAgain"/>).
    /// <para>
    /// Stored encrypted all the same, so a copy of the database alone does not hand out working keys.
    /// The AES-GCM key is derived from <c>Jwt:Key</c>, the key that signs the tokens: it is already
    /// persisted (<c>IndxData/jwt.key</c> or configuration), and if it changes every token is invalid
    /// anyway, so the two are lost together. ASP.NET Data Protection was the obvious alternative and
    /// the wrong one here: its key ring is not configured to persist, so a redeploy could silently
    /// make every stored key unreadable.
    /// </para>
    /// </summary>
    public static class ApiKeySealer
    {
        private const int NonceSize = 12;
        private const int TagSize = 16;
        private static readonly byte[] Info = Encoding.UTF8.GetBytes("Indx API key reveal v1");

        public static string Seal(string token, string jwtKey)
        {
            var plain = Encoding.UTF8.GetBytes(token);
            var sealedBytes = new byte[NonceSize + plain.Length + TagSize];
            var nonce = sealedBytes.AsSpan(0, NonceSize);
            RandomNumberGenerator.Fill(nonce);
            using var aes = new AesGcm(DeriveKey(jwtKey), TagSize);
            aes.Encrypt(nonce, plain, sealedBytes.AsSpan(NonceSize, plain.Length), sealedBytes.AsSpan(NonceSize + plain.Length, TagSize));
            return Convert.ToBase64String(sealedBytes);
        }

        /// <summary>The token, or null when the value is not one this key sealed.</summary>
        public static string? Open(string sealedValue, string jwtKey)
        {
            try
            {
                var bytes = Convert.FromBase64String(sealedValue);
                if (bytes.Length < NonceSize + TagSize) return null;
                var length = bytes.Length - NonceSize - TagSize;
                var plain = new byte[length];
                using var aes = new AesGcm(DeriveKey(jwtKey), TagSize);
                aes.Decrypt(bytes.AsSpan(0, NonceSize), bytes.AsSpan(NonceSize, length), bytes.AsSpan(NonceSize + length, TagSize), plain);
                return Encoding.UTF8.GetString(plain);
            }
            catch (Exception e) when (e is FormatException or CryptographicException)
            {
                return null;
            }
        }

        private static byte[] DeriveKey(string jwtKey) =>
            HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(jwtKey), 32, info: Info);
    }
}
