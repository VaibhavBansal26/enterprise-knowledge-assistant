using System.Collections.Frozen;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;

namespace Api.Web;

/// <summary>Masks sensitive query-string values before they reach the logs.</summary>
public static class LogRedactor
{
    public const string Mask = "[REDACTED]";

    private static readonly FrozenSet<string> SensitiveKeys = new[]
    {
        "token", "access_token", "id_token", "refresh_token", "code", "key", "apikey", "api_key",
        "password", "pwd", "secret", "authorization", "signature", "sig",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static string RedactQuery(QueryString query)
    {
        if (!query.HasValue)
        {
            return string.Empty;
        }

        var parsed = QueryHelpers.ParseQuery(query.Value);
        var parts = new List<string>();
        foreach (var (name, values) in parsed)
        {
            foreach (var value in values)
            {
                parts.Add($"{name}={(SensitiveKeys.Contains(name) ? Mask : value)}");
            }
        }

        return parts.Count == 0 ? string.Empty : "?" + string.Join('&', parts);
    }
}
