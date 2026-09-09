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
    public void Custom_patterns_are_honoured()
    {
        var redactor = new Redactor(["ssn", "iban"]);
        Assert.True(redactor.IsSensitiveName("CustomerIban"));
        Assert.False(redactor.IsSensitiveName("Password"));
    }
}
