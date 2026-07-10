using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace LearnMcpTutorial.Wpf.Services;

/// <summary>
/// Converts markdown text from the LLM answer into a WPF FlowDocument
/// with code blocks (dark IDE-style), inline code, headers, lists, and links.
/// </summary>
public static class MarkdownRenderer
{
    private static readonly SolidColorBrush CodeBlockBg = new(Color.FromRgb(0x1E, 0x1E, 0x1E));
    private static readonly SolidColorBrush CodeBlockBorder = new(Color.FromRgb(0x33, 0x33, 0x33));
    private static readonly SolidColorBrush InlineCodeBg = new(Color.FromRgb(0xF0, 0xF0, 0xF0));
    private static readonly SolidColorBrush InlineCodeFg = new(Color.FromRgb(0xC7, 0x25, 0x4E));
    private static readonly SolidColorBrush CodeFg = new(Color.FromRgb(0xD4, 0xD4, 0xD4));
    private static readonly SolidColorBrush AccentBlue = new(Color.FromRgb(0x56, 0x9C, 0xD6));
    private static readonly SolidColorBrush AccentGreen = new(Color.FromRgb(0x4E, 0xC9, 0xB0));
    private static readonly SolidColorBrush AccentYellow = new(Color.FromRgb(0xD7, 0xBA, 0x7D));
    private static readonly SolidColorBrush AccentOrange = new(Color.FromRgb(0xCE, 0x91, 0x78));
    private static readonly SolidColorBrush HeaderFg = new(Color.FromRgb(0x1A, 0x1A, 0x1A));

    // Simple syntax highlighting patterns for common languages
    private static readonly (string pattern, SolidColorBrush color)[] SyntaxPatterns =
    [
        (@"\b(using|namespace|class|public|private|protected|internal|static|void|string|int|bool|var|new|return|if|else|for|foreach|while|async|await|try|catch|throw|typeof|nameof|readonly|const|sealed|override|virtual|abstract)\b", AccentBlue),
        (@"\b(Add|Configure|Build|Run|Map|Use|Get|Set|Create|Read|Write|Open|Close)\b", AccentGreen),
        (@"(""[^""]*""|'[^']*')", AccentOrange),
        (@"\b(\d+\.?\d*)\b", AccentGreen),
        (@"//.*$|/\*[\s\S]*?\*/", AccentGreen),
        (@"\b(true|false|null)\b", AccentBlue),
        (@"\b(az|dotnet|docker|git|npm|nuget|kubectl|helm)\b", AccentYellow),
    ];

    public static FlowDocument Render(string markdown)
    {
        var doc = new FlowDocument
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13,
            PagePadding = new Thickness(0)
        };

        if (string.IsNullOrWhiteSpace(markdown))
        {
            doc.Blocks.Add(new Paragraph(new Run("(no answer)") { Foreground = Brushes.Gray }));
            return doc;
        }

        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        int i = 0;

        while (i < lines.Length)
        {
            var line = lines[i];

            // Code block: ``` ... ```
            if (line.TrimStart().StartsWith("```"))
            {
                var language = line.TrimStart()[3..].Trim();
                i++;
                var codeLines = new List<string>();
                while (i < lines.Length && !lines[i].TrimStart().StartsWith("```"))
                {
                    codeLines.Add(lines[i]);
                    i++;
                }
                i++; // skip closing ```
                doc.Blocks.Add(CreateCodeBlock(string.Join("\n", codeLines), language));
                doc.Blocks.Add(new Paragraph { Margin = new Thickness(0, 0, 0, 8) }); // spacer
                continue;
            }

            // ## Heading
            if (line.StartsWith("## "))
            {
                doc.Blocks.Add(CreateHeading(line[3..], 18));
                i++; continue;
            }
            if (line.StartsWith("### "))
            {
                doc.Blocks.Add(CreateHeading(line[4..], 15));
                i++; continue;
            }
            if (line.StartsWith("#### "))
            {
                doc.Blocks.Add(CreateHeading(line[5..], 13));
                i++; continue;
            }

            // Unordered list: - item or * item
            if (Regex.IsMatch(line, @"^\s*[-*]\s"))
            {
                var listItems = new List<string>();
                while (i < lines.Length && Regex.IsMatch(lines[i], @"^\s*[-*]\s"))
                {
                    listItems.Add(Regex.Replace(lines[i], @"^\s*[-*]\s+", ""));
                    i++;
                }
                doc.Blocks.Add(CreateBulletList(listItems));
                continue;
            }

            // Ordered list: 1. item
            if (Regex.IsMatch(line, @"^\s*\d+\.\s"))
            {
                var listItems = new List<string>();
                while (i < lines.Length && Regex.IsMatch(lines[i], @"^\s*\d+\.\s"))
                {
                    listItems.Add(Regex.Replace(lines[i], @"^\s*\d+\.\s+", ""));
                    i++;
                }
                doc.Blocks.Add(CreateNumberedList(listItems));
                continue;
            }

            // Horizontal rule
            if (line.Trim() is "---" or "***" or "___")
            {
                doc.Blocks.Add(new Paragraph(new Run("─".PadRight(40, '─')) { Foreground = Brushes.LightGray })
                { Margin = new Thickness(0, 4, 0, 4) });
                i++; continue;
            }

            // Blank line
            if (string.IsNullOrWhiteSpace(line))
            {
                i++; continue;
            }

            // Regular paragraph (collect until blank line)
            var paraLines = new List<string>();
            while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i])
                   && !lines[i].TrimStart().StartsWith("```")
                   && !lines[i].StartsWith("##")
                   && !Regex.IsMatch(lines[i], @"^\s*[-*]\s")
                   && !Regex.IsMatch(lines[i], @"^\s*\d+\.\s")
                   && lines[i].Trim() is not ("---" or "***" or "___"))
            {
                paraLines.Add(lines[i]);
                i++;
            }
            if (paraLines.Count > 0)
            {
                doc.Blocks.Add(CreateParagraph(string.Join(" ", paraLines)));
            }
        }

        return doc;
    }

    private static Block CreateCodeBlock(string code, string language)
    {
        var container = new BlockUIContainer();
        var border = new Border
        {
            Background = CodeBlockBg,
            BorderBrush = CodeBlockBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 0, 0, 0)
        };

        var stack = new StackPanel();

        // Language label
        if (!string.IsNullOrWhiteSpace(language))
        {
            stack.Children.Add(new TextBlock
            {
                Text = language.ToUpperInvariant(),
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80)),
                Margin = new Thickness(0, 0, 0, 6)
            });
        }

        // Code text with syntax highlighting
        var codeBlock = new TextBlock
        {
            FontFamily = new FontFamily("Cascadia Code, Consolas, Courier New, monospace"),
            FontSize = 12,
            Foreground = CodeFg,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 1.4
        };

        // Apply syntax highlighting
        var codeLower = code.ToLowerInvariant();
        var isBash = codeLower.Contains("az ") || codeLower.Contains("dotnet ") ||
                     codeLower.Contains("git ") || codeLower.Contains("docker ") ||
                     codeLower.Contains("npm ") || language.Equals("bash", StringComparison.OrdinalIgnoreCase) ||
                     language.Equals("sh", StringComparison.OrdinalIgnoreCase) ||
                     language.Equals("shell", StringComparison.OrdinalIgnoreCase) ||
                     language.Equals("azurecli", StringComparison.OrdinalIgnoreCase) ||
                     language.Equals("cli", StringComparison.OrdinalIgnoreCase);

        if (isBash)
        {
            // Bash/CLI highlighting
            AddHighlightedLines(codeBlock, code, BashPatterns);
        }
        else
        {
            // Generic code highlighting
            AddHighlightedLines(codeBlock, code, SyntaxPatterns);
        }

        stack.Children.Add(codeBlock);
        border.Child = stack;
        container.Child = border;
        return container;
    }

    private static readonly (string pattern, SolidColorBrush color)[] BashPatterns =
    [
        (@"\b(az|dotnet|docker|git|npm|nuget|kubectl|helm|winget|choco|brew|pwsh|python|node)\b", AccentYellow),
        (@"\b(create|delete|show|list|update|set|get|run|build|push|pull|start|stop|restart|apply|deploy|connect|login|logout)\b", AccentGreen),
        (@"\b(--\w[\w-]*|-\w)\b", AccentBlue),
        (@"(""[^""]*""|'[^']*')", AccentOrange),
        (@"\b(\d+)\b", AccentGreen),
        (@"#.*$", Brushes.DimGray),
    ];

    private static void AddHighlightedLines(TextBlock codeBlock, string code,
        (string pattern, SolidColorBrush color)[] patterns)
    {
        var lines = code.Split('\n');
        for (int li = 0; li < lines.Length; li++)
        {
            if (li > 0) codeBlock.Inlines.Add(new LineBreak());
            HighlightLine(codeBlock, lines[li], patterns);
        }
    }

    private static void HighlightLine(TextBlock codeBlock, string line,
        (string pattern, SolidColorBrush color)[] patterns)
    {
        if (string.IsNullOrEmpty(line))
        {
            codeBlock.Inlines.Add(new Run(" ") { Foreground = CodeFg });
            return;
        }

        // Find all matches with their positions
        var matches = new List<(int start, int length, SolidColorBrush color)>();
        foreach (var (pattern, color) in patterns)
        {
            foreach (Match m in Regex.Matches(line, pattern))
            {
                if (m.Success)
                {
                    matches.Add((m.Index, m.Length, color));
                }
            }
        }

        // Sort by position and remove overlaps
        matches.Sort((a, b) => a.start.CompareTo(b.start));
        var deduped = new List<(int start, int length, SolidColorBrush color)>();
        int lastEnd = 0;
        foreach (var m in matches)
        {
            if (m.start >= lastEnd)
            {
                deduped.Add(m);
                lastEnd = m.start + m.length;
            }
        }

        // Build runs
        int pos = 0;
        foreach (var (start, length, color) in deduped)
        {
            // Text before match
            if (start > pos)
            {
                codeBlock.Inlines.Add(new Run(line[pos..start]) { Foreground = CodeFg });
            }
            // Highlighted match
            codeBlock.Inlines.Add(new Run(line.Substring(start, length)) { Foreground = color });
            pos = start + length;
        }
        // Remaining text
        if (pos < line.Length)
        {
            codeBlock.Inlines.Add(new Run(line[pos..]) { Foreground = CodeFg });
        }
    }

    private static Paragraph CreateHeading(string text, double fontSize)
    {
        return new Paragraph(new Run(text) { Foreground = HeaderFg, FontWeight = FontWeights.Bold })
        {
            FontSize = fontSize,
            Margin = new Thickness(0, 12, 0, 4)
        };
    }

    private static Paragraph CreateParagraph(string text)
    {
        var para = new Paragraph { Margin = new Thickness(0, 0, 0, 6), LineHeight = 1.5 };
        AddFormattedInlines(para, text);
        return para;
    }

    private static Block CreateBulletList(List<string> items)
    {
        var list = new System.Windows.Documents.List { MarkerStyle = TextMarkerStyle.Disc, Margin = new Thickness(20, 4, 0, 8) };
        foreach (var item in items)
        {
            var listItem = new ListItem();
            var para = new Paragraph { Margin = new Thickness(0, 1, 0, 1) };
            AddFormattedInlines(para, item);
            listItem.Blocks.Add(para);
            list.ListItems.Add(listItem);
        }
        return list;
    }

    private static Block CreateNumberedList(List<string> items)
    {
        var list = new System.Windows.Documents.List { MarkerStyle = TextMarkerStyle.Decimal, Margin = new Thickness(20, 4, 0, 8) };
        foreach (var item in items)
        {
            var listItem = new ListItem();
            var para = new Paragraph { Margin = new Thickness(0, 1, 0, 1) };
            AddFormattedInlines(para, item);
            listItem.Blocks.Add(para);
            list.ListItems.Add(listItem);
        }
        return list;
    }

    /// <summary>
    /// Parses inline markdown formatting: **bold**, *italic*, `code`, [links](url).
    /// </summary>
    private static void AddFormattedInlines(Paragraph para, string text)
    {
        // Combined regex for all inline formats
        var pattern = @"(`[^`]+`)|(\*\*[^*]+\*\*)|(\*[^*]+\*)|(\[[^\]]+\]\([^)]+\))|(https?://[^\s\)]+)";
        int pos = 0;

        foreach (Match m in Regex.Matches(text, pattern))
        {
            // Text before match
            if (m.Index > pos)
            {
                para.Inlines.Add(new Run(text[pos..m.Index]));
            }

            var matchText = m.Value;

            if (matchText.StartsWith("`") && matchText.EndsWith("`"))
            {
                // Inline code
                para.Inlines.Add(new Run(matchText[1..^1])
                {
                    FontFamily = new FontFamily("Cascadia Code, Consolas, Courier New, monospace"),
                    Background = InlineCodeBg,
                    Foreground = InlineCodeFg,
                    FontSize = 12
                });
            }
            else if (matchText.StartsWith("**") && matchText.EndsWith("**"))
            {
                para.Inlines.Add(new Run(matchText[2..^2]) { FontWeight = FontWeights.Bold });
            }
            else if (matchText.StartsWith("*") && matchText.EndsWith("*"))
            {
                para.Inlines.Add(new Run(matchText[1..^1]) { FontStyle = FontStyles.Italic });
            }
            else if (matchText.StartsWith("["))
            {
                // [text](url)
                var linkMatch = Regex.Match(matchText, @"\[([^\]]+)\]\(([^)]+)\)");
                if (linkMatch.Success)
                {
                    var link = new Hyperlink(new Run(linkMatch.Groups[1].Value))
                    {
                        NavigateUri = new Uri(linkMatch.Groups[2].Value),
                        Foreground = Brushes.Blue,
                        TextDecorations = TextDecorations.Underline
                    };
                    link.RequestNavigate += (s, e) =>
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.ToString())
                        { UseShellExecute = true });
                        e.Handled = true;
                    };
                    para.Inlines.Add(link);
                }
                else
                {
                    para.Inlines.Add(new Run(matchText));
                }
            }
            else if (matchText.StartsWith("http"))
            {
                var link = new Hyperlink(new Run(matchText))
                {
                    NavigateUri = new Uri(matchText),
                    Foreground = Brushes.Blue,
                    TextDecorations = TextDecorations.Underline
                };
                link.RequestNavigate += (s, e) =>
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.ToString())
                    { UseShellExecute = true });
                    e.Handled = true;
                };
                para.Inlines.Add(link);
            }
            else
            {
                para.Inlines.Add(new Run(matchText));
            }

            pos = m.Index + m.Length;
        }

        // Remaining text
        if (pos < text.Length)
        {
            para.Inlines.Add(new Run(text[pos..]));
        }

        // Ensure paragraph is not empty
        if (!para.Inlines.Any())
        {
            para.Inlines.Add(new Run(text));
        }
    }
}
