using System.Text.RegularExpressions;
using Application.Chat;

namespace Application.Safety;

public sealed record GroundingResult(double Score, int SupportedSentences, int AssessedSentences);

/// <summary>
/// A cheap, offline check that an answer stays within its sources: each sentence of the answer is compared with
/// the passages it cites (or all supplied passages when it cites none), and counts as supported when most of its
/// meaningful words appear there. The score is the supported fraction.
///
/// This is a lexical heuristic. It reliably catches an answer that wanders off its sources and it never calls a
/// model, but a faithful paraphrase can score lower than a quotation, so treat it as a warning signal and tune the
/// threshold against the eval set (an LLM judge is the stronger, costlier option).
/// </summary>
public static partial class GroundingChecker
{
    private const double SentenceSupportThreshold = 0.6;
    private const int MinWordsToAssess = 4;

    private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "are", "was", "were", "has", "have", "had", "that", "this", "with", "from", "they", "their",
        "them", "there", "which", "when", "what", "who", "whom", "will", "would", "can", "could", "should", "may", "might",
        "not", "but", "you", "your", "our", "its", "also", "any", "all", "into", "than", "then", "such", "each", "per",
        "based", "documents", "document", "sources", "source", "according", "provided",
    };

    [GeneratedRegex(@"\[\d{1,3}\]")]
    private static partial Regex Marker();

    [GeneratedRegex(@"(?<=[.!?])\s+|\n+")]
    private static partial Regex SentenceBreak();

    [GeneratedRegex(@"[a-z0-9]+")]
    private static partial Regex Word();

    private static HashSet<string> Significant(string text) =>
        Word().Matches(text.ToLowerInvariant()).Select(m => m.Value).Where(w => w.Length > 2 && !Stopwords.Contains(w)).ToHashSet(StringComparer.Ordinal);

    public static GroundingResult Score(string answer, IReadOnlyList<PromptSource> sources)
    {
        ArgumentNullException.ThrowIfNull(answer);
        ArgumentNullException.ThrowIfNull(sources);

        var vocabularyByNumber = sources.ToDictionary(s => s.Number, s => Significant(s.Content));
        var allVocabulary = vocabularyByNumber.Values.SelectMany(v => v).ToHashSet(StringComparer.Ordinal);

        var assessed = 0;
        var supported = 0;
        foreach (var sentence in SentenceBreak().Split(answer))
        {
            var cited = Marker().Matches(sentence)
                .Select(m => int.Parse(m.Value.Trim('[', ']'), System.Globalization.CultureInfo.InvariantCulture))
                .Where(vocabularyByNumber.ContainsKey)
                .ToList();
            var words = Significant(Marker().Replace(sentence, " "));
            if (words.Count < MinWordsToAssess)
            {
                continue;
            }

            // Judge against the cited passages when the sentence cites; otherwise against everything supplied.
            var vocabulary = cited.Count > 0 ? cited.SelectMany(n => vocabularyByNumber[n]).ToHashSet(StringComparer.Ordinal) : allVocabulary;
            var found = words.Count(vocabulary.Contains);

            assessed++;
            if ((double)found / words.Count >= SentenceSupportThreshold)
            {
                supported++;
            }
        }

        // Nothing assessable (for example "I don't know") makes no claims, so there is nothing to ground.
        return new GroundingResult(assessed == 0 ? 1.0 : (double)supported / assessed, supported, assessed);
    }
}
