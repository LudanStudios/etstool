using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace ETSAssistant.WinUI3.Services;

/// <summary>Renders the Markdown subset commonly returned by the analysis model to safe printable HTML.</summary>
public static partial class MarkdownHtmlRenderer
{
    public static string ToHtml(string markdown)
    {
        string[] lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var html = new StringBuilder();
        int index = 0;
        while (index < lines.Length)
        {
            string line = lines[index];
            if (string.IsNullOrWhiteSpace(line)) { index++; continue; }

            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                index++;
                var code = new StringBuilder();
                while (index < lines.Length && !lines[index].TrimStart().StartsWith("```", StringComparison.Ordinal))
                    code.AppendLine(WebUtility.HtmlEncode(lines[index++]));
                if (index < lines.Length) index++;
                html.Append("<pre><code>").Append(code).Append("</code></pre>");
                continue;
            }

            Match heading = HeadingRegex().Match(line);
            if (heading.Success)
            {
                int level = heading.Groups[1].Length;
                html.Append("<h").Append(level).Append('>')
                    .Append(RenderInline(heading.Groups[2].Value.Trim().TrimEnd('#').Trim()))
                    .Append("</h").Append(level).Append('>');
                index++;
                continue;
            }

            if (HorizontalRuleRegex().IsMatch(line))
            {
                html.Append("<hr>");
                index++;
                continue;
            }

            if (IsTableHeader(lines, index))
            {
                AppendTable(html, lines, ref index);
                continue;
            }

            if (line.TrimStart().StartsWith('>'))
            {
                html.Append("<blockquote>");
                while (index < lines.Length && lines[index].TrimStart().StartsWith('>'))
                {
                    string quote = lines[index++].TrimStart()[1..].TrimStart();
                    html.Append("<p>").Append(RenderInline(quote)).Append("</p>");
                }
                html.Append("</blockquote>");
                continue;
            }

            Match listItem = ListItemRegex().Match(line);
            if (listItem.Success)
            {
                bool ordered = listItem.Groups[2].Success;
                string tag = ordered ? "ol" : "ul";
                html.Append('<').Append(tag).Append('>');
                while (index < lines.Length && (listItem = ListItemRegex().Match(lines[index])).Success &&
                       listItem.Groups[2].Success == ordered)
                {
                    html.Append("<li>").Append(RenderInline(listItem.Groups[3].Value.Trim())).Append("</li>");
                    index++;
                }
                html.Append("</").Append(tag).Append('>');
                continue;
            }

            var paragraph = new StringBuilder(line.Trim());
            index++;
            while (index < lines.Length && !string.IsNullOrWhiteSpace(lines[index]) && !StartsBlock(lines, index))
                paragraph.Append(' ').Append(lines[index++].Trim());
            html.Append("<p>").Append(RenderInline(paragraph.ToString())).Append("</p>");
        }
        return html.ToString();
    }

    private static bool StartsBlock(string[] lines, int index) =>
        HeadingRegex().IsMatch(lines[index]) || HorizontalRuleRegex().IsMatch(lines[index]) ||
        ListItemRegex().IsMatch(lines[index]) || lines[index].TrimStart().StartsWith('>') ||
        lines[index].TrimStart().StartsWith("```", StringComparison.Ordinal) || IsTableHeader(lines, index);

    private static bool IsTableHeader(string[] lines, int index) => index + 1 < lines.Length &&
        lines[index].Contains('|') && TableSeparatorRegex().IsMatch(lines[index + 1]);

    private static void AppendTable(StringBuilder html, string[] lines, ref int index)
    {
        string[] headers = SplitTableRow(lines[index++]);
        index++; // separator row
        html.Append("<table><thead><tr>");
        foreach (string cell in headers) html.Append("<th>").Append(RenderInline(cell)).Append("</th>");
        html.Append("</tr></thead><tbody>");
        while (index < lines.Length && lines[index].Contains('|') && !string.IsNullOrWhiteSpace(lines[index]))
        {
            html.Append("<tr>");
            foreach (string cell in SplitTableRow(lines[index++]))
                html.Append("<td>").Append(RenderInline(cell)).Append("</td>");
            html.Append("</tr>");
        }
        html.Append("</tbody></table>");
    }

    private static string[] SplitTableRow(string row) => row.Trim().Trim('|')
        .Split('|').Select(cell => cell.Trim()).ToArray();

    private static string RenderInline(string value)
    {
        string safe = WebUtility.HtmlEncode(value);
        safe = LinkRegex().Replace(safe, match =>
        {
            string label = match.Groups[1].Value;
            string url = WebUtility.HtmlDecode(match.Groups[2].Value);
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ||
                (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)) return label;
            return $"<a href=\"{WebUtility.HtmlEncode(uri.AbsoluteUri)}\">{label}</a>";
        });
        safe = InlineCodeRegex().Replace(safe, "<code>$1</code>");
        safe = BoldRegex().Replace(safe, "<strong>$1$2</strong>");
        safe = ItalicRegex().Replace(safe, "<em>$1$2</em>");
        safe = StrikeRegex().Replace(safe, "<del>$1</del>");
        return safe;
    }

    [GeneratedRegex(@"^(#{1,6})\s+(.+?)\s*#*\s*$")]
    private static partial Regex HeadingRegex();
    [GeneratedRegex(@"^\s*(?:---+|\*\*\*+|___+)\s*$")]
    private static partial Regex HorizontalRuleRegex();
    [GeneratedRegex(@"^\s*(?:([-+*])\s+|(\d+)[.)]\s+)(.*)$")]
    private static partial Regex ListItemRegex();
    [GeneratedRegex(@"^\s*\|?\s*:?-{3,}:?\s*(?:\|\s*:?-{3,}:?\s*)+\|?\s*$")]
    private static partial Regex TableSeparatorRegex();
    [GeneratedRegex(@"\[([^\]]+)\]\((https?://[^\s)]+)\)")]
    private static partial Regex LinkRegex();
    [GeneratedRegex(@"`([^`]+)`")]
    private static partial Regex InlineCodeRegex();
    [GeneratedRegex(@"\*\*(.+?)\*\*|__(.+?)__")]
    private static partial Regex BoldRegex();
    [GeneratedRegex(@"(?<!\*)\*([^*]+)\*(?!\*)|(?<!_)_([^_]+)_(?!_)")]
    private static partial Regex ItalicRegex();
    [GeneratedRegex(@"~~(.+?)~~")]
    private static partial Regex StrikeRegex();
}
