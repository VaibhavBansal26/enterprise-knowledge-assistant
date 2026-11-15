namespace Application.Safety;

/// <summary>Price per million tokens, in US dollars.</summary>
public sealed record ModelPrice(decimal InputPerMTok, decimal OutputPerMTok);

/// <summary>
/// Turns token counts into an estimated cost. Prices are configuration, not truth: providers change them, so the
/// defaults below (first-party list prices at the time of writing) can be overridden under <c>Pricing:Models</c>.
/// A model with no entry is priced at <see cref="Fallback"/>, deliberately high, so a new model can never look free.
/// </summary>
public static class PricingCatalog
{
    public static readonly IReadOnlyDictionary<string, ModelPrice> Defaults = new Dictionary<string, ModelPrice>(StringComparer.Ordinal)
    {
        ["claude-opus-5-5"] = new(4m, 20m),
        ["claude-opus-4-8"] = new(5m, 25m),
        ["claude-fable-5-2"] = new(8m, 40m),
        ["claude-sonnet-5-5"] = new(2m, 10m),
        ["claude-haiku-5-5"] = new(0.10m, 0.50m),
        ["extractive-fallback"] = new(0m, 0m),
        ["none"] = new(0m, 0m),
        ["hashing-v1"] = new(0m, 0m),
        ["hashing-v2"] = new(0m, 0m),
        ["openai:text-embedding-3-small:384"] = new(0.02m, 0m),
    };

    /// <summary>Used for any model without an explicit price.</summary>
    public static readonly ModelPrice Fallback = new(8m, 40m);

    public static ModelPrice PriceOf(string model, IReadOnlyDictionary<string, ModelPrice>? overrides = null)
    {
        if (overrides is not null && overrides.TryGetValue(model, out var custom))
        {
            return custom;
        }

        return Defaults.TryGetValue(model, out var known) ? known : Fallback;
    }

    public static decimal Cost(long inputTokens, long outputTokens, ModelPrice price) =>
        (inputTokens * price.InputPerMTok + outputTokens * price.OutputPerMTok) / 1_000_000m;
}
