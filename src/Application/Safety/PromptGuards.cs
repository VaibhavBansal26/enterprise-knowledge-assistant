using System.Text.RegularExpressions;

namespace Application.Safety;

/// <summary>
/// Defence in depth against prompt injection hidden in documents. Escaping and delimiting (see <c>PromptBuilder</c>)
/// keep document text from breaking out of its block, and the system prompt tells the model to treat it as data.
/// This adds a second layer: sentences that read like instructions to the model are removed before they reach it.
/// It is a heuristic filter, not a guarantee, which is why it sits alongside the other layers rather than replacing them.
/// </summary>
public static partial class InjectionScanner
{
    public const string Placeholder = "[removed: possible instruction]";

    private static readonly Regex[] Patterns =
    [
        IgnorePrevious(),
        Disregard(),
        ForgetAll(),
        RevealPrompt(),
        RoleOverride(),
        NewInstructions(),
        SystemLabel(),
        HideFromUser(),
        ChatTemplateTokens(),
        OverrideRules(),
    ];

    [GeneratedRegex(@"\bignore\s+(?:all\s+|any\s+|the\s+|your\s+)?(?:previous|prior|above|earlier|preceding)\s+(?:instructions?|prompts?|rules?|messages?|context)", RegexOptions.IgnoreCase)]
    private static partial Regex IgnorePrevious();

    [GeneratedRegex(@"\bdisregard\s+(?:all\s+|any\s+|the\s+|your\s+)?(?:previous|prior|above|earlier|system|safety)", RegexOptions.IgnoreCase)]
    private static partial Regex Disregard();

    [GeneratedRegex(@"\bforget\s+(?:everything|all|what)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ForgetAll();

    [GeneratedRegex(@"\b(?:reveal|show|print|repeat|output|leak|display|disclose)\s+(?:me\s+)?(?:your\s+|the\s+)?(?:system|hidden|initial|original|secret)\s+(?:prompt|instructions?|message|rules)", RegexOptions.IgnoreCase)]
    private static partial Regex RevealPrompt();

    [GeneratedRegex(@"\byou\s+are\s+now\s+(?:a|an|in|the)\b|\bfrom\s+now\s+on,?\s+you\b|\bpretend\s+(?:to\s+be|you\s+are)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RoleOverride();

    [GeneratedRegex(@"\b(?:new|updated|real|actual)\s+instructions?\s*[:\-]", RegexOptions.IgnoreCase)]
    private static partial Regex NewInstructions();

    [GeneratedRegex(@"(?:^|\n|\.\s)\s*(?:system|assistant|developer)\s*(?:prompt|message)?\s*:", RegexOptions.IgnoreCase)]
    private static partial Regex SystemLabel();

    [GeneratedRegex(@"\bdo\s+not\s+(?:tell|inform|mention|reveal\s+to)\s+the\s+user\b|\bwithout\s+(?:telling|informing)\s+the\s+user\b", RegexOptions.IgnoreCase)]
    private static partial Regex HideFromUser();

    [GeneratedRegex(@"<\|(?:im_start|im_end|system|user|assistant|endoftext)\|>|\[/?INST\]|<<SYS>>|(?:^|\n)#{2,3}\s*(?:system|instruction)s?\b", RegexOptions.IgnoreCase)]
    private static partial Regex ChatTemplateTokens();

    [GeneratedRegex(@"\b(?:override|bypass|disable)\s+(?:all\s+|your\s+|the\s+)?(?:safety|security|content|system)\s+(?:rules?|filters?|policy|policies|restrictions?|guardrails?)", RegexOptions.IgnoreCase)]
    private static partial Regex OverrideRules();

    [GeneratedRegex(@"(?<=[.!?])\s+|\n+")]
    private static partial Regex SentenceBreak();

    public static bool LooksLikeInjection(string text) => Patterns.Any(p => p.IsMatch(text));

    /// <summary>Replaces each instruction-like sentence with a placeholder, keeping the rest of the passage usable.</summary>
    public static (string Text, int Removed) Neutralize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!LooksLikeInjection(text))
        {
            return (text, 0);
        }

        var removed = 0;
        var pieces = SentenceBreak().Split(text);
        var kept = new List<string>(pieces.Length);
        foreach (var piece in pieces)
        {
            if (piece.Length > 0 && LooksLikeInjection(piece))
            {
                removed++;
                kept.Add(Placeholder);
            }
            else if (piece.Length > 0)
            {
                kept.Add(piece);
            }
        }

        return (string.Join(' ', kept), removed);
    }
}

/// <summary>Detects an answer that parrots the hidden system prompt back, a common goal of injection attacks.</summary>
public static partial class SystemPromptLeakDetector
{
    [GeneratedRegex(@"[a-z0-9]+")]
    private static partial Regex Word();

    private static List<string> Words(string text) => Word().Matches(text.ToLowerInvariant()).Select(m => m.Value).ToList();

    private static IEnumerable<string> Shingles(List<string> words, int size)
    {
        for (var i = 0; i + size <= words.Count; i++)
        {
            yield return string.Join(' ', words.Skip(i).Take(size));
        }
    }

    /// <summary>True when the answer repeats <paramref name="shingleWords"/> consecutive words of the system prompt.</summary>
    public static bool Leaks(string answer, string systemPrompt, int shingleWords = 8)
    {
        ArgumentNullException.ThrowIfNull(answer);
        ArgumentNullException.ThrowIfNull(systemPrompt);

        var secret = Shingles(Words(systemPrompt), shingleWords).ToHashSet(StringComparer.Ordinal);
        return Shingles(Words(answer), shingleWords).Any(secret.Contains);
    }
}
