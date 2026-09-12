using System.Globalization;
using System.Text;

namespace IndxServer.Services
{
#pragma warning disable 1591
    /// <summary>
    /// Normalises a free-text name into the single URL-safe team name used both as identity and
    /// URL segment. There is deliberately no separate "display name" — this is the only name a
    /// team has, so we constrain rather than duplicate it.
    /// </summary>
    public static class TeamSlug
    {
        public const int MaxLength = 50;

        /// <summary>
        /// Lowercases, strips diacritics (æøå → ae/oe/aa style handled by best-effort folding),
        /// replaces runs of non [a-z0-9] with a single '-', and trims leading/trailing '-'.
        /// Returns "team" if nothing usable remains.
        /// </summary>
        public static string Sanitize(string? input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return "team";

            // Fold common Norwegian letters to ASCII before generic diacritic stripping.
            var pre = input.Trim().ToLowerInvariant()
                .Replace("æ", "ae").Replace("ø", "oe").Replace("å", "aa");

            var decomposed = pre.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(decomposed.Length);
            var lastWasDash = false;
            foreach (var ch in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
                    continue; // drop accents

                if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9'))
                {
                    sb.Append(ch);
                    lastWasDash = false;
                }
                else if (!lastWasDash && sb.Length > 0)
                {
                    sb.Append('-');
                    lastWasDash = true;
                }
            }

            var slug = sb.ToString().Trim('-');
            if (slug.Length > MaxLength)
                slug = slug[..MaxLength].Trim('-');

            return slug.Length == 0 ? "team" : slug;
        }
    }
#pragma warning restore 1591
}
