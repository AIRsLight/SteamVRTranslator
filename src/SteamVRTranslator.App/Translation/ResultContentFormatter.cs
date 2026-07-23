using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Documents;
using System.Windows.Media;
using HtmlAgilityPack;
using MdXaml;

namespace SteamVRTranslator.App.Translation;

internal static partial class ResultContentFormatter
{
    private static readonly HashSet<string> RemovedHtmlElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "iframe", "frame", "frameset", "object", "embed", "applet",
        "form", "input", "button", "textarea", "select", "option", "link",
        "base", "img", "svg", "canvas", "video", "audio", "source"
    };

    private static readonly HashSet<string> AllowedHtmlAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "class", "id", "style", "colspan", "rowspan", "dir", "lang", "width", "height"
    };

    private static readonly HashSet<string> BlockElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "address", "article", "aside", "blockquote", "div", "dl", "dt", "dd",
        "figcaption", "figure", "footer", "h1", "h2", "h3", "h4", "h5", "h6",
        "header", "li", "main", "nav", "ol", "p", "pre", "section", "table",
        "thead", "tbody", "tfoot", "tr", "th", "td", "ul"
    };

    public static FlowDocument CreateMarkdownDocument(string markdown)
    {
        var document = new Markdown().Transform(markdown ?? string.Empty);
        document.FontFamily = new FontFamily("Microsoft YaHei UI");
        document.FontSize = 18;
        document.Foreground = new SolidColorBrush(Color.FromRgb(244, 248, 246));
        document.PagePadding = new Thickness(14, 12, 10, 12);
        document.ColumnWidth = double.PositiveInfinity;
        return document;
    }

    public static string ExtractMarkdownVisibleText(string markdown)
    {
        var document = CreateMarkdownDocument(markdown);
        var range = new TextRange(document.ContentStart, document.ContentEnd);
        return NormalizeVisibleText(range.Text);
    }

    public static PreparedHtmlDocument PrepareHtml(string html)
    {
        var source = RemoveCodeFence(html);
        var document = new HtmlDocument
        {
            OptionFixNestedTags = true,
            OptionAutoCloseOnEnd = true
        };
        document.LoadHtml(source);

        var removable = document.DocumentNode
            .DescendantsAndSelf()
            .Where(node =>
                node.NodeType == HtmlNodeType.Comment ||
                node.NodeType == HtmlNodeType.Element && RemovedHtmlElements.Contains(node.Name))
            .ToArray();
        foreach (var node in removable)
        {
            node.Remove();
        }

        foreach (var node in document.DocumentNode.DescendantsAndSelf()
                     .Where(node => node.NodeType == HtmlNodeType.Element))
        {
            foreach (var attribute in node.Attributes.ToArray())
            {
                if (!AllowedHtmlAttributes.Contains(attribute.Name) ||
                    attribute.Name.StartsWith("on", StringComparison.OrdinalIgnoreCase))
                {
                    node.Attributes.Remove(attribute);
                    continue;
                }

                if (string.Equals(attribute.Name, "style", StringComparison.OrdinalIgnoreCase))
                {
                    attribute.Value = SanitizeCss(attribute.Value);
                }
            }
        }

        var styleNodes = document.DocumentNode
            .Descendants("style")
            .ToArray();
        var styles = string.Join(
            Environment.NewLine,
            styleNodes.Select(node => SanitizeCss(node.InnerText)));
        foreach (var styleNode in styleNodes)
        {
            styleNode.Remove();
        }

        var body = document.DocumentNode.SelectSingleNode("//body") ?? document.DocumentNode;
        var visibleText = ExtractHtmlVisibleText(body);
        var bodyHtml = body == document.DocumentNode ? body.InnerHtml : body.InnerHtml;
        var sanitized =
            "<!doctype html><html><head><meta charset=\"utf-8\">" +
            "<meta http-equiv=\"Content-Security-Policy\" " +
            "content=\"default-src 'none'; img-src data:; font-src data:; style-src 'unsafe-inline'\">" +
            "<style>" + styles + "\n" +
            "*{box-sizing:border-box}html,body{width:100%;height:100%;margin:0}body{overflow:hidden!important}</style>" +
            "</head><body>" + bodyHtml + "</body></html>";
        return new PreparedHtmlDocument(sanitized, visibleText);
    }

    public static string ExtractHtmlVisibleText(string html)
    {
        try
        {
            return PrepareHtml(html).VisibleText;
        }
        catch
        {
            return NormalizeVisibleText(html);
        }
    }

    public static string NormalizeVisibleText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var cleaned = new StringBuilder(text.Length);
        foreach (var character in text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'))
        {
            if (character == '\n')
            {
                cleaned.Append('\n');
            }
            else if (character == '\t')
            {
                cleaned.Append(' ');
            }
            else if (!char.IsControl(character))
            {
                cleaned.Append(character);
            }
        }

        var output = new StringBuilder(cleaned.Length);
        var blankLinePending = false;
        foreach (var rawLine in cleaned.ToString().Split('\n'))
        {
            var line = HorizontalWhitespace().Replace(rawLine, " ").Trim();
            if (line.Length == 0)
            {
                blankLinePending = output.Length > 0;
                continue;
            }

            if (output.Length > 0)
            {
                output.Append(blankLinePending ? "\n\n" : "\n");
            }
            output.Append(line);
            blankLinePending = false;
        }
        return output.ToString();
    }

    private static string ExtractHtmlVisibleText(HtmlNode root)
    {
        var text = new StringBuilder();
        AppendVisibleText(root, text);
        return NormalizeVisibleText(HtmlEntity.DeEntitize(text.ToString()));
    }

    private static void AppendVisibleText(HtmlNode node, StringBuilder output)
    {
        if (node.NodeType == HtmlNodeType.Text)
        {
            output.Append(node.InnerText);
            return;
        }

        if (node.NodeType != HtmlNodeType.Document && node.NodeType != HtmlNodeType.Element)
        {
            return;
        }

        if (string.Equals(node.Name, "br", StringComparison.OrdinalIgnoreCase))
        {
            output.AppendLine();
            return;
        }

        var block = BlockElements.Contains(node.Name);
        if (block && output.Length > 0)
        {
            output.AppendLine();
        }
        foreach (var child in node.ChildNodes)
        {
            AppendVisibleText(child, output);
        }
        if (block)
        {
            output.AppendLine();
        }
    }

    private static string RemoveCodeFence(string html)
    {
        var trimmed = (html ?? string.Empty).Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var firstLineEnd = trimmed.IndexOf('\n');
        var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        return firstLineEnd >= 0 && lastFence > firstLineEnd
            ? trimmed[(firstLineEnd + 1)..lastFence].Trim()
            : trimmed;
    }

    private static string SanitizeCss(string css)
    {
        var sanitized = CssImport().Replace(css ?? string.Empty, string.Empty);
        sanitized = CssUrl().Replace(sanitized, "none");
        sanitized = CssExpression().Replace(sanitized, string.Empty);
        sanitized = CssUnsafeProperty().Replace(sanitized, string.Empty);
        return sanitized;
    }

    [GeneratedRegex(@"[\t\f\v ]+", RegexOptions.CultureInvariant)]
    private static partial Regex HorizontalWhitespace();

    [GeneratedRegex(@"@import\s+[^;]+;?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CssImport();

    [GeneratedRegex(@"url\s*\([^)]*\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CssUrl();

    [GeneratedRegex(@"expression\s*\([^)]*\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CssExpression();

    [GeneratedRegex(@"(?:behavior|-moz-binding)\s*:[^;]+;?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CssUnsafeProperty();
}

internal sealed record PreparedHtmlDocument(string Html, string VisibleText);
