using PropertyAuditTool.Services;
using Xunit;

namespace PropertyAuditTool.Tests;

public class AuditValueEvaluatorTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("\t \r\n", true)]
    public void IsEmpty_TreatsNullAndWhitespaceAsEmpty(object? value, bool expectedEmpty)
    {
        var (matches, isEmpty) = AuditValueEvaluator.Evaluate("isEmpty", value, null);
        Assert.Equal(expectedEmpty, isEmpty);
        Assert.Equal(expectedEmpty, matches);
    }

    [Theory]
    [InlineData("text")]
    [InlineData(0)]
    [InlineData(false)]
    [InlineData("{\"items\":[]}")]
    [InlineData("<p></p>")]
    public void IsEmpty_NonBlankValuesAreFilled(object value)
    {
        var (matches, isEmpty) = AuditValueEvaluator.Evaluate("isEmpty", value, null);
        Assert.False(isEmpty);
        Assert.False(matches);
    }

    [Fact]
    public void HasText_WhitespaceIsNotText()
    {
        var (matches, isEmpty) = AuditValueEvaluator.Evaluate("hasText", "  ", null);
        Assert.True(isEmpty);
        Assert.False(matches);
    }

    [Theory]
    [InlineData("Hello World", "world", true)]
    [InlineData("Hello World", "HELLO", true)]
    [InlineData("Hello World", "bye", false)]
    public void Contains_IsCaseInsensitiveSubstring(string value, string search, bool expected)
    {
        var (matches, _) = AuditValueEvaluator.Evaluate("contains", value, search);
        Assert.Equal(expected, matches);
    }

    [Theory]
    [InlineData("Exact", "exact", true)]
    [InlineData("Exact", "Exacto", false)]
    [InlineData(" padded ", "padded", false)] // whitespace is significant
    [InlineData("padded", " padded ", false)]
    public void Equals_IsCaseInsensitiveExact(string value, string search, bool expected)
    {
        var (matches, _) = AuditValueEvaluator.Evaluate("equals", value, search);
        Assert.Equal(expected, matches);
    }

    [Fact]
    public void NumericZeroAndFalseAreFilled()
    {
        var (zeroMatches, zeroEmpty) = AuditValueEvaluator.Evaluate("hasText", 0, null);
        var (falseMatches, falseEmpty) = AuditValueEvaluator.Evaluate("hasText", false, null);
        Assert.True(zeroMatches);
        Assert.False(zeroEmpty);
        Assert.True(falseMatches);
        Assert.False(falseEmpty);
    }

    [Fact]
    public void UnknownOperatorThrows()
    {
        Assert.Throws<InvalidOperationException>(
            () => AuditValueEvaluator.Evaluate("startsWith", "x", null));
    }
}
