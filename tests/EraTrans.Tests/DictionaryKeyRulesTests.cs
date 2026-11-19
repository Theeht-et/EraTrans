using EraTrans.Core;
using Xunit;

namespace EraTrans.Tests;

public sealed class DictionaryKeyRulesTests
{
    [Theory]
    [InlineData("文本ERB\\OPENING00001", true)]
    [InlineData("变量ERB\\OPENING00001", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsTranslatableTextKey_UsesTextPrefixOnly(string? key, bool expected)
    {
        Assert.Equal(expected, DictionaryKeyRules.IsTranslatableTextKey(key));
    }
}
