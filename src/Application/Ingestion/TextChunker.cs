using System.Text;

namespace Application.Ingestion;

/// <summary>
/// Splits text into overlapping passages of bounded size, preferring natural boundaries:
/// paragraphs first, then sentences, then words. Overlap carries context across chunk edges so a fact
/// that straddles a boundary is still retrievable.
/// </summary>
public static class TextChunker
{
    public static IReadOnlyList<string> Chunk(string text, int maxChars = 1000, int overlapChars = 150)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxChars, 100);
        ArgumentOutOfRangeException.ThrowIfNegative(overlapChars);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(overlapChars, maxChars / 2);

        var units = SplitIntoUnits(Normalize(text), maxChars);
        var chunks = new List<string>();
        var current = new StringBuilder();
        var hasNewContent = false; // false while `current` holds only overlap carried from the previous chunk

        foreach (var unit in units)
        {
            if (current.Length > 0 && current.Length + 1 + unit.Length > maxChars)
            {
                var finished = current.ToString().Trim();
                chunks.Add(finished);
                current.Clear().Append(OverlapTail(finished, overlapChars));
                hasNewContent = false;
            }

            if (current.Length > 0)
            {
                current.Append(' ');
            }

            current.Append(unit);
            hasNewContent = true;
        }

        var last = current.ToString().Trim();
        if (hasNewContent && last.Length > 0)
        {
            chunks.Add(last);
        }

        return chunks;
    }

    private static string Normalize(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace("\0", string.Empty, StringComparison.Ordinal);

    /// <summary>Paragraphs, with any paragraph larger than the limit broken into sentences or words.</summary>
    private static IEnumerable<string> SplitIntoUnits(string text, int maxChars)
    {
        foreach (var paragraph in text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var flat = string.Join(' ', paragraph.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            if (flat.Length <= maxChars)
            {
                yield return flat;
                continue;
            }

            foreach (var piece in SplitLong(flat, maxChars))
            {
                yield return piece;
            }
        }
    }

    private static IEnumerable<string> SplitLong(string text, int maxChars)
    {
        var buffer = new StringBuilder();
        foreach (var sentence in SplitSentences(text))
        {
            if (sentence.Length > maxChars)
            {
                if (buffer.Length > 0)
                {
                    yield return buffer.ToString();
                    buffer.Clear();
                }

                foreach (var part in SplitWords(sentence, maxChars))
                {
                    yield return part;
                }

                continue;
            }

            if (buffer.Length > 0 && buffer.Length + 1 + sentence.Length > maxChars)
            {
                yield return buffer.ToString();
                buffer.Clear();
            }

            if (buffer.Length > 0)
            {
                buffer.Append(' ');
            }

            buffer.Append(sentence);
        }

        if (buffer.Length > 0)
        {
            yield return buffer.ToString();
        }
    }

    private static IEnumerable<string> SplitSentences(string text)
    {
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is '.' or '!' or '?' && (i + 1 == text.Length || char.IsWhiteSpace(text[i + 1])))
            {
                yield return text[start..(i + 1)].Trim();
                start = i + 1;
            }
        }

        if (start < text.Length)
        {
            var rest = text[start..].Trim();
            if (rest.Length > 0)
            {
                yield return rest;
            }
        }
    }

    /// <summary>Last resort for text with no sentence breaks: split on words, hard-cutting giant tokens.</summary>
    private static IEnumerable<string> SplitWords(string text, int maxChars)
    {
        var buffer = new StringBuilder();
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var remaining = word;
            while (remaining.Length > maxChars)
            {
                if (buffer.Length > 0)
                {
                    yield return buffer.ToString();
                    buffer.Clear();
                }

                yield return remaining[..maxChars];
                remaining = remaining[maxChars..];
            }

            if (buffer.Length > 0 && buffer.Length + 1 + remaining.Length > maxChars)
            {
                yield return buffer.ToString();
                buffer.Clear();
            }

            if (buffer.Length > 0)
            {
                buffer.Append(' ');
            }

            buffer.Append(remaining);
        }

        if (buffer.Length > 0)
        {
            yield return buffer.ToString();
        }
    }

    /// <summary>The last ~<paramref name="overlap"/> characters of a chunk, starting at a word boundary.</summary>
    private static string OverlapTail(string chunk, int overlap)
    {
        if (overlap == 0 || chunk.Length <= overlap)
        {
            return overlap == 0 ? string.Empty : chunk;
        }

        var start = chunk.Length - overlap;
        var space = chunk.IndexOf(' ', start);
        return space < 0 ? string.Empty : chunk[(space + 1)..];
    }
}
