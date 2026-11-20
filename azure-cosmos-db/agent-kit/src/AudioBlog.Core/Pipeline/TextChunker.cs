using System.Text.RegularExpressions;

namespace AudioBlog.Core.Pipeline;

/// <summary>
/// Splits text into chunks of at most <c>maxChars</c>, preferring paragraph, then sentence, then word boundaries.
/// Deterministic: the same text and limit always produce the same chunks, which is what makes
/// "resume at chunk N" after a crash safe.
/// </summary>
public static partial class TextChunker
{
    public static IReadOnlyList<string> Split(string text, int maxChars)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxChars, 100);

        var pieces = new List<(string Text, bool NewParagraph)>();
        foreach (var paragraph in ParagraphBreak().Split(text.Trim()))
        {
            var p = paragraph.Trim();
            if (p.Length == 0) continue;
            if (p.Length <= maxChars) { pieces.Add((p, true)); continue; }

            var first = true;
            foreach (var sentence in SentenceBreak().Split(p))
            {
                var parts = sentence.Length <= maxChars ? [sentence] : SplitWords(sentence, maxChars);
                foreach (var part in parts)
                {
                    pieces.Add((part, first));
                    first = false;
                }
            }
        }

        // Greedily pack the pieces back together; paragraphs keep their blank line.
        var chunks = new List<string>();
        var current = "";
        foreach (var (piece, newParagraph) in pieces)
        {
            var joined = current.Length == 0 ? piece : current + (newParagraph ? "\n\n" : " ") + piece;
            if (joined.Length <= maxChars)
            {
                current = joined;
                continue;
            }
            chunks.Add(current);
            current = piece;
        }
        if (current.Length > 0)
            chunks.Add(current);
        return chunks;
    }

    private static List<string> SplitWords(string text, int maxChars)
    {
        var result = new List<string>();
        var start = 0;
        while (start < text.Length)
        {
            var length = Math.Min(maxChars, text.Length - start);
            if (start + length < text.Length)
            {
                var space = text.LastIndexOf(' ', start + length - 1, length);
                if (space > start) length = space - start;
            }
            var part = text.Substring(start, length).Trim();
            if (part.Length > 0) result.Add(part);
            start += length;
        }
        return result;
    }

    [GeneratedRegex(@"\n\s*\n")]
    private static partial Regex ParagraphBreak();

    [GeneratedRegex(@"(?<=[.!?…])\s+")]
    private static partial Regex SentenceBreak();
}
