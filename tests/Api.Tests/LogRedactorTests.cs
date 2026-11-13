using Api.Web;
using Microsoft.AspNetCore.Http;

namespace Api.Tests;

public sealed class LogRedactorTests
{
    [Fact]
    public void Sensitive_values_are_masked_and_others_kept()
    {
        var result = LogRedactor.RedactQuery(new QueryString("?q=hello&access_token=abc123&Password=hunter2&page=2"));

        Assert.Equal($"?q=hello&access_token={LogRedactor.Mask}&Password={LogRedactor.Mask}&page=2", result);
        Assert.DoesNotContain("abc123", result, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Repeated_sensitive_keys_are_all_masked()
    {
        var result = LogRedactor.RedactQuery(new QueryString("?key=one&key=two"));

        Assert.Equal($"?key={LogRedactor.Mask}&key={LogRedactor.Mask}", result);
    }

    [Fact]
    public void Empty_query_yields_empty_string()
    {
        Assert.Equal(string.Empty, LogRedactor.RedactQuery(QueryString.Empty));
    }
}
