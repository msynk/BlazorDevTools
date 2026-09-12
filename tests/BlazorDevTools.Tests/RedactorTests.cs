using BlazorDevTools.Inspection;

namespace BlazorDevTools.Tests;

public class RedactorTests
{
    [Theory]
    [InlineData("Password", true)]
    [InlineData("accessToken", true)]
    [InlineData("X-Api-Key", true)]
    [InlineData("ConnectionString", true)]
    [InlineData("Authorization", true)]
    [InlineData("UserName", false)]
    [InlineData("Total", false)]
    public void Sensitive_names_are_matched_case_insensitively(string name, bool expected)
    {
        Assert.Equal(expected, Redactor.Default.IsSensitiveName(name));
    }

    [Fact]
    public void Query_string_values_with_sensitive_keys_are_redacted()
    {
        var url = "https://example.com/api/login?user=ada&token=abc123&page=2";
        var redacted = Redactor.Default.RedactUrl(url);
        Assert.Equal("https://example.com/api/login?user=ada&token=" + Redactor.RedactedValue + "&page=2", redacted);
        Assert.Equal("https://example.com/plain", Redactor.Default.RedactUrl("https://example.com/plain"));
    }

    [Fact]
    public void Jwt_values_are_redacted_even_under_innocent_query_names()
    {
        var jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.signature";
        var redacted = Redactor.Default.RedactUrl("https://example.com/callback?session=" + jwt + "&page=2");

        Assert.Equal("https://example.com/callback?session=" + Redactor.RedactedValue + "&page=2", redacted);
        Assert.Equal("https://example.com/callback?auth=" + Redactor.RedactedValue,
            Redactor.Default.RedactUrl("https://example.com/callback?auth=Bearer+opaque"));
    }

    [Fact]
    public void Custom_patterns_are_honoured()
    {
        var redactor = new Redactor(["ssn", "iban"]);
        Assert.True(redactor.IsSensitiveName("CustomerIban"));
        Assert.False(redactor.IsSensitiveName("Password"));
    }
}
