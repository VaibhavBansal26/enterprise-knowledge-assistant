using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Application.Chat;

namespace Infrastructure.Llm;

/// <summary>
/// Offline stand-in for a language model: it answers by quoting the sentences of the supplied sources that best
/// match the question, with the same [n] citations a real model would write. Deterministic and free, so the
/// whole chat stack (retrieval, streaming, citations, caching) runs without an API key. It does not reason or
/// paraphrase. Set <c>Llm:Provider=anthropic</c> for real answers.
/// </summary>
public sealed partial class ExtractiveLlmClient : ILlmClient
{
    public const string Model = "extractive-fallback";

    private const string NoAnswer = "The provided sources do not contain enough information to answer that.";

    public string ModelId => Model;

    [GeneratedRegex("<source id=\"(\\d+)\" title=\"([^\"]*)\">\\s*(.*?)\\s*</source>", RegexOptions.Singleline)]
    private static partial Regex SourceBlock();

    [GeneratedRegex("[a-z0-9]+")]
    private static partial Regex Word();

    public async IAsyncEnumerable<LlmEvent> StreamAsync(
        LlmRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var last = request.Messages[^1].Content;
        var questionIndex = last.LastIndexOf("Question:", StringComparison.Ordinal);
        var question = questionIndex >= 0 ? last[(questionIndex + "Question:".Length)..].Trim() : string.Empty;
        var questionWords = Words(question);

        var candidates = new List<(int Number, string Sentence, int Score)>();
        foreach (Match source in SourceBlock().Matches(last))
        {
            var number = int.Parse(source.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            var text = WebUtility.HtmlDecode(source.Groups[3].Value);
            foreach (var sentence in Regex.Split(text, @"(?<=[.!?])\s+").Select(s => s.Trim()).Where(s => s.Length > 0))
            {
                var overlap = Words(sentence).Intersect(questionWords).Count();
                if (overlap > 0)
                {
                    candidates.Add((number, sentence, overlap));
                }
            }
        }

        var best = candidates.OrderByDescending(c => c.Score).ThenBy(c => c.Number).Take(3).ToList();
        var answer = best.Count == 0
            ? NoAnswer
            : "Based on the documents: " + string.Join(' ', best.Select(c => $"{c.Sentence.TrimEnd()} [{c.Number}]"));

        var tokens = 0;
        foreach (var piece in Chunks(answer))
        {
            cancellationToken.ThrowIfCancellationRequested();
            tokens++;
            yield return new LlmTextDelta(piece);
            await Task.Yield();
        }

        yield return new LlmCompleted(Model, request.Messages.Sum(m => TokenBudget.Estimate(m.Content)), tokens, "end_turn");
    }

    private static HashSet<string> Words(string text) =>
        Word().Matches(text.ToLowerInvariant()).Select(m => m.Value).Where(w => w.Length > 2).ToHashSet(StringComparer.Ordinal);

    /// <summary>Splits into small word groups so the response streams like a model's would.</summary>
    private static IEnumerable<string> Chunks(string text)
    {
        var words = text.Split(' ');
        var buffer = new StringBuilder();
        for (var i = 0; i < words.Length; i++)
        {
            buffer.Append(words[i]);
            if (i < words.Length - 1)
            {
                buffer.Append(' ');
            }

            if ((i + 1) % 4 == 0 || i == words.Length - 1)
            {
                yield return buffer.ToString();
                buffer.Clear();
            }
        }
    }
}
