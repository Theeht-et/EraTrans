using EraTrans.Core;

namespace EraTrans.Tests;

public sealed class DictionaryKeyRulesTests
{
    [Theory]
    [InlineData("文本ERB\u005cOPENING00001", true)]
    [InlineData("变量ERB\u005cOPENING00001", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsTranslatableTextKey_UsesTextPrefixOnly(string? key, bool expected)
    {
        Assert.Equal(expected, DictionaryKeyRules.IsTranslatableTextKey(key));
    }
}
