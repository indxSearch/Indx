using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;

namespace Indx.Systm.Blazor
{
    /// <summary>The little markdown an assistant's answer may carry (bold, italic, code, a list,
    /// paragraphs), rendered from text that is encoded first, so nothing in an answer reaches the
    /// page as markup. For the console's Agent panel and findings and the Observr admin's Asks page
    /// alike. Not a markdown engine, and not meant to grow into one.</summary>
    public static partial class ChatMarkdown
    {
        public static MarkupString Render(string text)
        {
            var sb = new StringBuilder();
            var inList = false;
            var paragraph = new StringBuilder();
            void Flush()
            {
                if (paragraph.Length == 0) return;
                sb.Append("<p>").Append(Inline(paragraph.ToString())).Append("</p>");
                paragraph.Clear();
            }
            foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
            {
                var line = raw.TrimEnd();
                var item = ListItem().Match(line);
                if (item.Success)
                {
                    Flush();
                    if (!inList) { sb.Append("<ul>"); inList = true; }
                    sb.Append("<li>").Append(Inline(item.Groups[1].Value)).Append("</li>");
                    continue;
                }
                if (inList) { sb.Append("</ul>"); inList = false; }
                if (line.Length == 0) { Flush(); continue; }
                if (paragraph.Length > 0) paragraph.Append(' ');
                paragraph.Append(line.Trim());
            }
            if (inList) sb.Append("</ul>");
            Flush();
            return new MarkupString(sb.ToString());
        }

        private static string Inline(string s)
        {
            var encoded = WebUtility.HtmlEncode(s);
            encoded = Code().Replace(encoded, "<code>$1</code>");
            encoded = Bold().Replace(encoded, "<strong>$1</strong>");
            encoded = Italic().Replace(encoded, "<em>$1</em>");
            return encoded;
        }

        [GeneratedRegex(@"^\s*[-*•]\s+(.*)$")] private static partial Regex ListItem();
        [GeneratedRegex(@"`([^`]+)`")] private static partial Regex Code();
        [GeneratedRegex(@"\*\*([^*]+)\*\*")] private static partial Regex Bold();
        [GeneratedRegex(@"(?<![*\w])\*([^*\s][^*]*?)\*(?![*\w])")] private static partial Regex Italic();
    }
}
