using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace AudioBlog.Core.Pipeline.Extraction;

/// <summary>
/// Converts cleaned article HTML into plain text that keeps the structure visible to the rewrite model:
/// "## Heading", "- list item", "[Table] | a | b | [/Table]", "[Code] ... [/Code]", "[Image: alt text]".
/// Plain TextContent would flatten tables into word salad and silently drop images.
/// </summary>
public static partial class StructuredText
{
    public static string FromHtml(string html)
    {
        var document = new HtmlParser().ParseDocument($"<body>{html}</body>");
        var sb = new StringBuilder();
        Walk(document.Body!, sb, listDepth: 0);
        return Normalize(sb.ToString());
    }

    private static void Walk(INode node, StringBuilder sb, int listDepth)
    {
        foreach (var child in node.ChildNodes)
        {
            if (child is IText text)
            {
                sb.Append(CollapseWhitespace().Replace(text.Data, " "));
                continue;
            }
            if (child is not IElement el)
                continue;

            switch (el.LocalName)
            {
                case "script" or "style" or "noscript" or "template" or "svg" or "button" or "form" or "iframe":
                    break;
                case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                    Block(sb, "## " + Inline(el));
                    break;
                case "p":
                    Block(sb, Inline(el));
                    break;
                case "br":
                    sb.Append('\n');
                    break;
                case "hr":
                    Block(sb, "");
                    break;
                case "ul" or "ol":
                    BlockStart(sb);
                    List(el, sb, listDepth);
                    sb.Append('\n');
                    break;
                case "table":
                    Block(sb, Table(el));
                    break;
                case "pre":
                    Block(sb, "[Code]\n" + el.TextContent.Trim('\n') + "\n[/Code]");
                    break;
                case "blockquote":
                    Block(sb, "[Quote] " + Inline(el) + " [/Quote]");
                    break;
                case "img":
                    var alt = el.GetAttribute("alt")?.Trim();
                    if (!string.IsNullOrEmpty(alt))
                        Block(sb, $"[Image: {alt}]");
                    break;
                case "figure":
                    var img = el.QuerySelector("img");
                    var caption = el.QuerySelector("figcaption");
                    if (el.QuerySelector("table") is { } figTable)
                    {
                        Block(sb, Table(figTable));
                    }
                    else if (el.QuerySelector("pre") is { } figCode)
                    {
                        Block(sb, "[Code]\n" + figCode.TextContent.Trim('\n') + "\n[/Code]");
                    }
                    else
                    {
                        var altText = img?.GetAttribute("alt")?.Trim();
                        var captionText = caption is null ? null : Inline(caption);
                        var parts = new[] { altText, captionText }.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList();
                        if (parts.Count > 0)
                            Block(sb, $"[Image: {string.Join(" – ", parts)}]");
                    }
                    break;
                case "div" or "section" or "article" or "main" or "header" or "footer" or "aside" or "dl" or "dd" or "dt" or "details" or "summary":
                    BlockStart(sb);
                    Walk(el, sb, listDepth);
                    BlockStart(sb);
                    break;
                default:
                    Walk(el, sb, listDepth);
                    break;
            }
        }
    }

    private static void List(IElement list, StringBuilder sb, int depth)
    {
        var ordered = list.LocalName == "ol";
        var n = 0;
        foreach (var li in list.Children.Where(c => c.LocalName == "li"))
        {
            n++;
            var nested = li.Children.Where(c => c.LocalName is "ul" or "ol").ToList();
            foreach (var sub in nested)
                sub.Remove();
            sb.Append(new string(' ', depth * 2)).Append(ordered ? $"{n}. " : "- ").Append(Inline(li)).Append('\n');
            foreach (var sub in nested)
                List(sub, sb, depth + 1);
        }
    }

    private static string Table(IElement table)
    {
        var sb = new StringBuilder("[Table]");
        if (table.QuerySelector("caption") is { } caption)
            sb.Append(' ').Append(Inline(caption));
        sb.Append('\n');
        foreach (var row in table.QuerySelectorAll("tr"))
        {
            var cells = row.Children.Where(c => c.LocalName is "td" or "th").Select(Inline).ToList();
            if (cells.Count > 0)
                sb.Append("| ").Append(string.Join(" | ", cells)).Append(" |\n");
        }
        return sb.Append("[/Table]").ToString();
    }

    private static string Inline(IElement el)
    {
        var sb = new StringBuilder();
        foreach (var node in el.ChildNodes)
        {
            switch (node)
            {
                case IText text:
                    sb.Append(text.Data);
                    break;
                case IElement { LocalName: "br" }:
                    sb.Append(' ');
                    break;
                case IElement { LocalName: "img" } img when !string.IsNullOrWhiteSpace(img.GetAttribute("alt")):
                    sb.Append(" [Image: ").Append(img.GetAttribute("alt")!.Trim()).Append("] ");
                    break;
                case IElement { LocalName: "code" } code:
                    sb.Append('`').Append(code.TextContent).Append('`');
                    break;
                case IElement { LocalName: "script" or "style" or "svg" or "button" }:
                    break;
                case IElement child:
                    sb.Append(Inline(child));
                    break;
            }
        }
        return CollapseWhitespace().Replace(sb.ToString(), " ").Trim();
    }

    private static void Block(StringBuilder sb, string content)
    {
        BlockStart(sb);
        if (content.Length > 0)
            sb.Append(content).Append("\n\n");
    }

    private static void BlockStart(StringBuilder sb)
    {
        if (sb.Length > 0 && sb[^1] != '\n')
            sb.Append("\n\n");
    }

    private static string Normalize(string text)
    {
        var lines = text.Split('\n').Select(l => l.TrimEnd());
        var joined = string.Join('\n', lines);
        return ManyNewlines().Replace(joined, "\n\n").Trim();
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex CollapseWhitespace();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ManyNewlines();
}
