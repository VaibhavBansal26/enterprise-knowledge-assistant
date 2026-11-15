using System.Globalization;
using System.Text;

namespace Infrastructure;

/// <summary>
/// Managed platforms (Render, Heroku, Fly) hand out connection <em>URIs</em> such as <c>postgres://user:pw@host:5432/db</c>
/// and <c>redis://host:6379</c>. Npgsql and StackExchange.Redis want their own formats, so URIs are converted here and
/// anything else passes through unchanged.
/// </summary>
public static class ConnectionStrings
{
    public static string Postgres(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!value.Contains("://", StringComparison.Ordinal) || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("postgres" or "postgresql"))
        {
            return value;
        }

        var parts = new StringBuilder();
        parts.Append(CultureInfo.InvariantCulture, $"Host={uri.Host};");
        if (!uri.IsDefaultPort && uri.Port > 0)
        {
            parts.Append(CultureInfo.InvariantCulture, $"Port={uri.Port};");
        }

        var database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
        if (database.Length > 0)
        {
            parts.Append(CultureInfo.InvariantCulture, $"Database={database};");
        }

        if (uri.UserInfo.Length > 0)
        {
            var colon = uri.UserInfo.IndexOf(':', StringComparison.Ordinal);
            var user = colon < 0 ? uri.UserInfo : uri.UserInfo[..colon];
            parts.Append(CultureInfo.InvariantCulture, $"Username={Uri.UnescapeDataString(user)};");
            if (colon >= 0)
            {
                // Quoted so a password containing ';' or '=' cannot inject further keywords.
                var password = Uri.UnescapeDataString(uri.UserInfo[(colon + 1)..]).Replace("'", "''", StringComparison.Ordinal);
                parts.Append(CultureInfo.InvariantCulture, $"Password='{password}';");
            }
        }

        // Honour ?sslmode= (Neon and most managed providers send sslmode=require); otherwise use TLS when offered.
        parts.Append(CultureInfo.InvariantCulture, $"SSL Mode={SslMode(uri.Query)}");
        return parts.ToString();
    }

    private static string SslMode(string query)
    {
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            if (kv.Length == 2 && kv[0].Equals("sslmode", StringComparison.OrdinalIgnoreCase))
            {
                return kv[1].ToLowerInvariant() switch
                {
                    "disable" => "Disable",
                    "require" => "Require",
                    "verify-ca" => "VerifyCA",
                    "verify-full" => "VerifyFull",
                    _ => "Prefer",
                };
            }
        }

        return "Prefer";
    }

    public static string Redis(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        // "redis:6379" (host:port) also parses as a URI with scheme "redis", so require the "://" of a real URI.
        if (!value.Contains("://", StringComparison.Ordinal) || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("redis" or "rediss"))
        {
            return value;
        }

        var options = new StringBuilder();
        options.Append(CultureInfo.InvariantCulture, $"{uri.Host}:{(uri.Port > 0 ? uri.Port : 6379)}");
        if (uri.Scheme == "rediss")
        {
            options.Append(",ssl=true");
        }

        if (uri.UserInfo.Length > 0)
        {
            var colon = uri.UserInfo.IndexOf(':', StringComparison.Ordinal);
            var password = Uri.UnescapeDataString(colon < 0 ? uri.UserInfo : uri.UserInfo[(colon + 1)..]);
            options.Append(CultureInfo.InvariantCulture, $",password={password}");
        }

        return options.ToString();
    }
}
