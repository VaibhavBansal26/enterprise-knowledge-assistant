using System.Text.RegularExpressions;

namespace Application.Safety;

public sealed record RedactionResult(string Text, IReadOnlyDictionary<string, int> ByKind)
{
    public int Count => ByKind.Values.Sum();
}

/// <summary>
/// Masks personal data and secrets in free text before it is stored, sent to a third-party model, or logged.
/// Deliberately conservative: it prefers masking a few harmless look-alikes over letting a real card number,
/// SSN or key through. Card numbers and IBANs are only masked when their check digits are valid, which keeps
/// order numbers and phone-like digit runs out of the way.
/// </summary>
public static partial class PiiRedactor
{
    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\b")]
    private static partial Regex Jwt();

    [GeneratedRegex(@"\b(?:AKIA|ASIA)[0-9A-Z]{16}\b|\b(?:sk|pk|rk)-[A-Za-z0-9_-]{16,}|\bgh[pousr]_[A-Za-z0-9]{20,}|\bxox[abprs]-[A-Za-z0-9-]{10,}")]
    private static partial Regex ApiKey();

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}")]
    private static partial Regex Email();

    [GeneratedRegex(@"(?<![\d-])(?:\d[ -]?){12,18}\d(?![\d-])")]
    private static partial Regex CardCandidate();

    [GeneratedRegex(@"\b[A-Z]{2}\d{2}(?: ?[A-Z0-9]{4}){2,7}(?: ?[A-Z0-9]{1,4})?\b")]
    private static partial Regex IbanCandidate();

    [GeneratedRegex(@"(?<![\d-])(?!000|666|9\d\d)\d{3}-(?!00)\d{2}-(?!0000)\d{4}(?![\d-])")]
    private static partial Regex Ssn();

    [GeneratedRegex(@"(?<![\w.])(?:\+\d{1,3}[ .-]?)?(?:\(\d{3}\)[ .-]?|\d{3}[ .-])\d{3}[ .-]\d{4}(?![\w])|(?<![\w.])\+\d{1,3}[ .-]?\d{2,4}[ .-]?\d{3,4}[ .-]?\d{3,4}(?![\w])")]
    private static partial Regex Phone();

    public static RedactionResult Redact(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        string Apply(string input, Regex regex, string kind, string mask, Func<string, bool>? accept = null)
        {
            return regex.Replace(input, match =>
            {
                if (accept is not null && !accept(match.Value))
                {
                    return match.Value;
                }

                counts[kind] = counts.GetValueOrDefault(kind) + 1;
                return mask;
            });
        }

        // Longer, more specific patterns first so a card number is not half-eaten as a phone number.
        var result = Apply(text, Jwt(), "token", "[TOKEN]");
        result = Apply(result, ApiKey(), "secret", "[SECRET]");
        result = Apply(result, Email(), "email", "[EMAIL]");
        result = Apply(result, CardCandidate(), "card", "[CARD]", LuhnValid);
        result = Apply(result, IbanCandidate(), "iban", "[IBAN]", IbanValid);
        result = Apply(result, Ssn(), "ssn", "[SSN]");
        result = Apply(result, Phone(), "phone", "[PHONE]");

        return new RedactionResult(result, counts);
    }

    /// <summary>Luhn checksum over the digits of a candidate card number (13-19 digits).</summary>
    public static bool LuhnValid(string candidate)
    {
        var digits = candidate.Where(char.IsDigit).Select(c => c - '0').ToArray();
        if (digits.Length is < 13 or > 19)
        {
            return false;
        }

        var sum = 0;
        for (var i = 0; i < digits.Length; i++)
        {
            var d = digits[digits.Length - 1 - i];
            if (i % 2 == 1)
            {
                d *= 2;
                if (d > 9)
                {
                    d -= 9;
                }
            }

            sum += d;
        }

        return sum % 10 == 0;
    }

    /// <summary>ISO 13616 mod-97 check.</summary>
    public static bool IbanValid(string candidate)
    {
        var iban = new string(candidate.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        if (iban.Length is < 15 or > 34)
        {
            return false;
        }

        var rearranged = iban[4..] + iban[..4];
        var remainder = 0;
        foreach (var ch in rearranged)
        {
            var value = char.IsDigit(ch) ? ch - '0' : ch - 'A' + 10;
            remainder = (value >= 10 ? remainder * 100 + value : remainder * 10 + value) % 97;
        }

        return remainder == 1;
    }
}
