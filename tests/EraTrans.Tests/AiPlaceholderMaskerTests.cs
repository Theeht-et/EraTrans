using EraTrans.Core;
using Xunit;

namespace EraTrans.Tests;

public sealed class AiPlaceholderMaskerTests
{
    [Fact]
    public void MaskAndRestore_ProtectsJapaneseBraceVariableNames()
    {
        const string original = "恭順({SOURCE:恭順})";

        MaskedLineText masked = AiPlaceholderMasker.Mask(original);

        Assert.Equal("恭順([[P0]])", masked.MaskedText);
        Assert.Single(masked.Placeholders);
        Assert.Equal("{SOURCE:恭順}", masked.Placeholders[0].Original);

        string restored = AiPlaceholderMasker.Restore("恭顺([[P0]])", masked.Placeholders);

        Assert.Equal("恭顺({SOURCE:恭順})", restored);
        Assert.Equal(string.Empty, TranslationPreValidator.Validate(original, restored, isScriptTarget: true));
    }

    [Fact]
    public void MaskAndRestore_HandlesAlignmentAndNestedParentheses()
    {
        const string original = "仕掛けたエサ：%ITEMNAME:(GET_MUSHIDATA(MUSHI_TRAP, \"エサ番号\"))% [{商品ID, 3}] %ITEMNAME:商品ID, 18, LEFT%";

        MaskedLineText masked = AiPlaceholderMasker.Mask(original);

        Assert.Equal("仕掛けたエサ：[[P0]] [[[P1]]] [[P2]]", masked.MaskedText);
        Assert.Equal(3, masked.Placeholders.Count);
        Assert.Equal("%ITEMNAME:(GET_MUSHIDATA(MUSHI_TRAP, \"エサ番号\"))%", masked.Placeholders[0].Original);
        Assert.Equal("{商品ID, 3}", masked.Placeholders[1].Original);
        Assert.Equal("%ITEMNAME:商品ID, 18, LEFT%", masked.Placeholders[2].Original);

        string restored = AiPlaceholderMasker.Restore(
            "放置的诱饵：[[P0]] [[[P1]]] [[P2]]",
            masked.Placeholders);

        Assert.Equal(
            "放置的诱饵：%ITEMNAME:(GET_MUSHIDATA(MUSHI_TRAP, \"エサ番号\"))% [{商品ID, 3}] %ITEMNAME:商品ID, 18, LEFT%",
            restored);
    }

    [Fact]
    public void MaskAndRestore_ProtectsTernaryConditionWhileExposingBranches()
    {
        const string original = @"%CALLNAME:ARG%の%BASENAME:強化能力%が一時的に{ABS(変動値)}\@ 変動値 > 0 ? 上がった # 下がった \@";

        MaskedLineText masked = AiPlaceholderMasker.Mask(original);

        Assert.Equal(@"[[P0]]の[[P1]]が一時的に[[P2]]\@ [[P3]] ? 上がった # 下がった \@", masked.MaskedText);
        Assert.Equal(4, masked.Placeholders.Count);
        Assert.Equal("%CALLNAME:ARG%", masked.Placeholders[0].Original);
        Assert.Equal("%BASENAME:強化能力%", masked.Placeholders[1].Original);
        Assert.Equal("{ABS(変動値)}", masked.Placeholders[2].Original);
        Assert.Equal("変動値 > 0", masked.Placeholders[3].Original);

        string restored = AiPlaceholderMasker.Restore(
            @"[[P0]]的[[P1]]暂时[[P2]]\@ [[P3]] ? 提升了 # 下降了 \@",
            masked.Placeholders);

        Assert.Equal(
            @"%CALLNAME:ARG%的%BASENAME:強化能力%暂时{ABS(変動値)}\@ 変動値 > 0 ? 提升了 # 下降了 \@",
            restored);
    }

    [Fact]
    public void Mask_IgnoresLiteralPercentagesAndEscapedDelimiters()
    {
        const string original = @"成功率が50%から80%に上昇（\{固定\}・\%表示\%）";

        MaskedLineText masked = AiPlaceholderMasker.Mask(original);

        Assert.Equal(original, masked.MaskedText);
        Assert.Empty(masked.Placeholders);
    }

    [Fact]
    public void Mask_AvoidsTokenCollisionWithExistingText()
    {
        const string original = "示例[[P0]]与{SOURCE:恭順}";

        MaskedLineText masked = AiPlaceholderMasker.Mask(original);

        Assert.Equal("示例[[P0]]与[[P1]]", masked.MaskedText);
        Assert.Equal("示例[[P0]]与{SOURCE:恭順}", AiPlaceholderMasker.Restore(masked.MaskedText, masked.Placeholders));
    }

    [Fact]
    public void Restore_ToleratesWhitespaceAndLowercaseTokenDrift()
    {
        MaskedLineText masked = AiPlaceholderMasker.Mask("恭順({SOURCE:恭順})");

        string restored = AiPlaceholderMasker.Restore("恭顺([[ p0 ]])", masked.Placeholders);

        Assert.Equal("恭顺({SOURCE:恭順})", restored);
    }

    [Fact]
    public async Task TranslateWindowAsync_MasksPromptAndRestoresExactPlaceholders()
    {
        var fakeClient = new RecordingAiClient("""{"1":"恭顺([[P0]])","2":"[[P0]]提升了[[P1]]"}""");
        var service = new AiTranslationService(fakeClient);
        var profile = new AiProviderProfile
        {
            Id = "test",
            Name = "Test",
            BaseUrl = "https://example.invalid/v1",
            ApiKey = "sk-test",
            Model = "gpt-test"
        };
        var window = new TranslationWindow(
            "@SHOW_SOURCE",
            1,
            [
                new WindowLine(1, "k1", "恭順({SOURCE:恭順})", "@SHOW_SOURCE", true, false, 0, false, null),
                new WindowLine(2, "k2", "%CALLNAME:MASTER%の{EXP:絶頂経験}", "@SHOW_SOURCE", true, false, 0, false, null)
            ]);

        WindowTranslationOutcome outcome = await service.TranslateWindowAsync(profile, window);

        Assert.DoesNotContain("{SOURCE:恭順}", fakeClient.LastUserPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("{EXP:絶頂経験}", fakeClient.LastUserPrompt, StringComparison.Ordinal);
        Assert.Contains("[[P0]]", fakeClient.LastUserPrompt, StringComparison.Ordinal);

        Assert.Equal(2, outcome.Translations.Count);
        Assert.Equal("恭顺({SOURCE:恭順})", outcome.Translations[0].Translation);
        Assert.Equal("%CALLNAME:MASTER%提升了{EXP:絶頂経験}", outcome.Translations[1].Translation);
        Assert.Equal(
            string.Empty,
            TranslationPreValidator.Validate("恭順({SOURCE:恭順})", outcome.Translations[0].Translation, isScriptTarget: true));
    }

    private sealed class RecordingAiClient(string response) : IAiChatCompletionClient
    {
        public string LastUserPrompt { get; private set; } = string.Empty;

        public Task<string> CompleteAsync(
            AiProviderProfile profile,
            string systemPrompt,
            string userPrompt,
            CancellationToken cancellationToken)
        {
            LastUserPrompt = userPrompt;
            return Task.FromResult(response);
        }
    }
}
