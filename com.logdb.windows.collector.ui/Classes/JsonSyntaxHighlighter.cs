using System.Text.RegularExpressions;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace com.logdb.windows.collector.ui.Classes;

/// <summary>
/// Live JSON syntax highlighter for AvaloniaEdit.
/// Recolors visible lines on every document change — drop the instance into
/// the editor's TextArea.TextView.LineTransformers and it just works.
/// Color palette mirrors VS Code's "Dark+" defaults.
/// </summary>
public sealed class JsonSyntaxHighlighter : DocumentColorizingTransformer
{
    // "key" followed (after optional whitespace) by a colon → property key
    private static readonly Regex PropertyKey = new(@"""(?:\\.|[^""\\])*""(?=\s*:)", RegexOptions.Compiled);
    // any remaining double-quoted string is a value
    private static readonly Regex StringLiteral = new(@"""(?:\\.|[^""\\])*""", RegexOptions.Compiled);
    private static readonly Regex Number = new(@"(?<![A-Za-z_])-?\d+(\.\d+)?([eE][+-]?\d+)?", RegexOptions.Compiled);
    private static readonly Regex BoolNullLiteral = new(@"\b(true|false|null)\b", RegexOptions.Compiled);

    private static readonly IBrush KeyBrush = new SolidColorBrush(Color.Parse("#9CDCFE"));    // VS-light-blue
    private static readonly IBrush StringBrush = new SolidColorBrush(Color.Parse("#CE9178")); // VS-orange
    private static readonly IBrush NumberBrush = new SolidColorBrush(Color.Parse("#B5CEA8")); // VS-light-green
    private static readonly IBrush LiteralBrush = new SolidColorBrush(Color.Parse("#569CD6")); // VS-blue

    protected override void ColorizeLine(DocumentLine line)
    {
        if (line == null || line.Length == 0)
            return;

        var lineText = CurrentContext.Document.GetText(line.Offset, line.Length);

        try
        {
            // Order matters: keys first (so subsequent StringLiteral pass skips them),
            // then string values, then numbers, then literals.
            ApplyMatches(line, lineText, PropertyKey, KeyBrush, skipAlreadyColored: false);
            ApplyMatches(line, lineText, StringLiteral, StringBrush, skipAlreadyColored: true);
            ApplyMatches(line, lineText, Number, NumberBrush, skipAlreadyColored: true);
            ApplyMatches(line, lineText, BoolNullLiteral, LiteralBrush, skipAlreadyColored: true);
        }
        catch
        {
            // Highlighting is cosmetic — never throw out of the render pipeline.
        }
    }

    private void ApplyMatches(DocumentLine line, string lineText, Regex pattern, IBrush brush, bool skipAlreadyColored)
    {
        foreach (Match match in pattern.Matches(lineText))
        {
            // For non-key passes, avoid re-coloring spans already painted as keys.
            // Cheap heuristic: a string literal that ends just before a ':' is a key.
            if (skipAlreadyColored && pattern == StringLiteral)
            {
                var after = match.Index + match.Length;
                var rest = lineText.AsSpan(after).TrimStart();
                if (rest.Length > 0 && rest[0] == ':')
                    continue;
            }

            ChangeLinePart(
                line.Offset + match.Index,
                line.Offset + match.Index + match.Length,
                part => part.TextRunProperties.SetForegroundBrush(brush));
        }
    }
}
