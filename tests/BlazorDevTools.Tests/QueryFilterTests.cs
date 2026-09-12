using BlazorDevTools.UI;

namespace BlazorDevTools.Tests;

public class QueryFilterTests
{
    [Fact]
    public void Parses_tokens_thresholds_and_free_text()
    {
        var filter = QueryFilter.Parse("Product method:GET renders>20");

        Assert.Equal(["Product"], filter.Terms);
        Assert.Equal("GET", filter.Token("method"));
        Assert.Equal(20, filter.IntToken("renders>"));
        Assert.True(filter.MatchesText("ProductList"));
        Assert.False(filter.MatchesText("OrderList"));
    }

    [Fact]
    public void Urls_remain_search_terms_instead_of_becoming_tokens()
    {
        var filter = QueryFilter.Parse("https://example.test/api/orders");

        Assert.Equal(["https://example.test/api/orders"], filter.Terms);
        Assert.Empty(filter.Tokens);
        Assert.True(filter.MatchesText("GET https://example.test/api/orders"));
    }
}
