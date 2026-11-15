using System.Text.RegularExpressions;

namespace Application.Chat;

/// <summary>
/// Shared word handling for the offline stand-ins (hashing embeddings and the extractive model): lower-case words
/// with common English function words dropped and a light plural-stripping stem, so "passwords" matches "password"
/// and "the", "for" or "is" carry no weight. Real embedding models do not need this; it only makes the stand-ins
/// stop treating filler words as evidence of relevance.
/// </summary>
public static partial class Lexicon
{
    private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        "a", "an", "and", "are", "as", "at", "be", "been", "being", "but", "by", "can", "could", "did", "do", "does", "for",
        "from", "had", "has", "have", "how", "i", "if", "in", "into", "is", "it", "its", "may", "me", "my", "no", "not", "of",
        "on", "or", "our", "should", "so", "than", "that", "the", "their", "them", "then", "there", "these", "they", "this",
        "those", "to", "was", "we", "were", "what", "when", "where", "which", "who", "whom", "why", "will", "with", "would",
        "you", "your", "all", "any", "each", "per", "also", "must", "about", "up", "out", "over", "after", "before",
    };

    [GeneratedRegex("[a-z0-9]+")]
    private static partial Regex Word();

    public static IEnumerable<string> Tokens(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        foreach (Match m in Word().Matches(text.ToLowerInvariant()))
        {
            var word = m.Value;
            if (word.Length < 2 || Stopwords.Contains(word))
            {
                continue;
            }

            yield return Stem(word);
        }
    }

    /// <summary>Strips common endings (plural s, -ed, -ing) so related word forms share one token.</summary>
    public static string Stem(string word) => word switch
    {
        _ when word.Length > 6 && word.EndsWith("ing", StringComparison.Ordinal) => word[..^3],
        _ when word.Length > 5 && word.EndsWith("ed", StringComparison.Ordinal) => word[..^2],
        _ when word.Length > 4 && word.EndsWith("ies", StringComparison.Ordinal) => word[..^3] + "y",
        _ when word.Length > 4 && word.EndsWith("sses", StringComparison.Ordinal) => word[..^2],
        _ when word.Length > 3 && word.EndsWith('s') && !word.EndsWith("ss", StringComparison.Ordinal) && !word.EndsWith("us", StringComparison.Ordinal) => word[..^1],
        _ => word,
    };
}
