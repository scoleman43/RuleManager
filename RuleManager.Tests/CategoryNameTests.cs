using RuleManager.Core.Domain;

namespace RuleManager.Tests;

public class CategoryNameTests
{
    [Theory]
    [InlineData("Auto:Insurance", "Auto:Insurance")]
    [InlineData(" Auto : Insurance ", "Auto:Insurance")]
    [InlineData("Software", "Software")]
    public void Normalize_ReturnsCanonicalCategory(string input, string expected)
    {
        Assert.Equal(expected, CategoryName.Normalize(input));
    }

    [Fact]
    public void HasSubCategory_RecognizesColonFormat()
    {
        Assert.True(CategoryName.HasSubCategory("Auto:Insurance"));
        Assert.False(CategoryName.HasSubCategory("Insurance"));
    }
}
