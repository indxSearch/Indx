using System.Text;

namespace IndxServer.Services
{
    /// <summary>
    /// The value and range operands of a filter key, for the Browsing statistics
    /// (Notes/statistics-design.md, "Browsing"). A combined key is unique per visitor's click path,
    /// so it is counted per operand: <c>genre = Poetry</c> is the unit a merchant thinks in.
    ///
    /// The grammar is the library's (CLAUDE.md, "the token IS the filter"):
    /// <c>VF;field;value</c>, <c>VFC;field;value</c>, <c>RF;field;min;max;culture</c>, combined as
    /// <c>\0 left OP right \u0001</c> with OP one of <c>&amp; | !</c>. Structural characters
    /// inside a field name or value are always percent-escaped, so an operand is exactly a run of
    /// characters between brackets and operators, and no bracket matching is needed.
    ///
    /// A negated operand (<c>!VF;…</c>, C# only; HTTP has no NOT) is skipped: "not Poetry" is
    /// not narrowing by Poetry. Anything unrecognised is skipped too - the key was accepted by
    /// the search that recorded it, so this only reads it, and a reader must never throw.
    /// </summary>
    public static class FilterKeyOperands
    {
        /// <summary>One operand. <c>Value</c> is empty for a range, which is reported by field.</summary>
        public readonly record struct Operand(string Field, string Value);

        public static IEnumerable<Operand> Parse(string? key)
        {
            if (string.IsNullOrEmpty(key)) yield break;
            int i = 0;
            while (i < key.Length)
            {
                if (IsStructural(key[i])) { i++; continue; }
                int start = i;
                while (i < key.Length && !IsStructural(key[i])) i++;
                if (start > 0 && key[start - 1] == '!') continue;

                var parts = key[start..i].Split(';');
                if (parts.Length >= 3 && parts[0] is "VF" or "VFC")
                    yield return new Operand(Unescape(parts[1]), Unescape(parts[2]));
                else if (parts.Length >= 2 && parts[0] == "RF")
                    yield return new Operand(Unescape(parts[1]), string.Empty);
            }
        }

        private static bool IsStructural(char c) => c is '\0' or '\u0001' or '&' or '|' or '!';

        /// <summary>A copy of <c>Indx.Json.FilterKey.Unescape</c>, which is internal to the
        /// library. Decodes only the exact codes Escape emits; any other '%' stays literal.</summary>
        private static string Unescape(string s)
        {
            if (!s.Contains('%')) return s;
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '%' && i + 2 < s.Length)
                {
                    char? decoded = (s[i + 1], s[i + 2]) switch
                    {
                        ('2', '5') => '%',
                        ('3', 'B') => ';',
                        ('2', '6') => '&',
                        ('7', 'C') => '|',
                        ('2', '1') => '!',
                        ('0', '0') => '\0',
                        ('0', '1') => '\u0001',
                        _ => null
                    };
                    if (decoded.HasValue) { sb.Append(decoded.Value); i += 2; continue; }
                }
                sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
