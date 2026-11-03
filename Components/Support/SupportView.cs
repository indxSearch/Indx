using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;

namespace IndxServer.Components.Support
{
    /// <summary>How the Support pages show a ticket: its status as a chip, and a message's text with
    /// its links clickable and nothing else interpreted. The Portal's Admin → Support has the same
    /// (IndxLicensePortal/Components/Support/SupportFormat.cs); the two apps share no code.</summary>
    public static partial class SupportView
    {
        /// <summary>The chip for a status as the Portal sends it. Answered is the strong one: it is
        /// the one that wants the reader.</summary>
        public static (string Label, string Color, string Text) Status(string status) => status switch
        {
            "waitingOnUs" => ("Waiting on Indx", "var(--lv3)", "var(--lv8)"),
            "answered" => ("Answered", "var(--lv8)", "var(--lv0)"),
            _ => ("Closed", "var(--lv2)", "var(--lv5)"),
        };

        /// <summary>The kind as a column says it.</summary>
        public static string Kind(string kind) => kind switch
        {
            "feature" => "Feature request",
            "feedback" => "Feedback",
            _ => "Support",
        };

        [GeneratedRegex(@"https?://[^\s<>""']+", RegexOptions.IgnoreCase)]
        private static partial Regex Link();

        /// <summary>Plain text as a fragment: the text as text (Blazor encodes it), an http(s) address
        /// as a link that opens beside the page. Nothing anyone wrote is rendered as markup.</summary>
        public static RenderFragment Text(string body) => builder =>
        {
            // Fixed sequence numbers, as a Razor loop compiles to: each pass has the same shape.
            var at = 0;
            foreach (Match m in Link().Matches(body))
            {
                if (m.Index > at) builder.AddContent(0, body[at..m.Index]);
                var url = m.Value.TrimEnd('.', ',', ')', ';', ':');
                builder.OpenElement(1, "a");
                builder.AddAttribute(2, "href", url);
                builder.AddAttribute(3, "target", "_blank");
                builder.AddAttribute(4, "rel", "noopener noreferrer nofollow");
                builder.AddContent(5, url);
                builder.CloseElement();
                at = m.Index + url.Length;
            }
            if (at < body.Length) builder.AddContent(6, body[at..]);
        };
    }
}
