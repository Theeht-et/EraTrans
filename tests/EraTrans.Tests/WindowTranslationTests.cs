using System.Text.Json;
using EraTrans.Core;
using Xunit;

namespace EraTrans.Tests;

public sealed class WindowTranslationTests
{
    [Fact]
    public void Payload_SendsSceneAndPerLineCommandAndKind()
    {
        TranslationWindow window = new(
            "EVENTFIRST",
            1,
            [
                new WindowLine(1, "k1", "移動する", "EVENTFIRST", true, false, 0, false, null, "PRINTBUTTON", "button", "EVENTFIRST#0001")
            ],
            SceneId: "EVENTFIRST#0001",
            SceneType: "menu");

        using JsonDocument document = JsonDocument.Parse(AiTranslationService.BuildWindowPayload(window));
        JsonElement root = document.RootElement;

        JsonElement scene = root.GetProperty("scene");
        Assert.Equal("EVENTFIRST#0001", scene.GetProperty("id").GetString());
        Assert.Equal("menu", scene.GetProperty("type").GetString());

        JsonElement line = root.GetProperty("lines").EnumerateArray().Single();
        Assert.Equal("PRINTBUTTON", line.GetProperty("cmd").GetString());
        Assert.Equal("button", line.GetProperty("kind").GetString());
        Assert.Equal("EVENTFIRST#0001", line.GetProperty("sid").GetString());
    }

    [Fact]
    public void Payload_SendsLineOrderWithTranslateAndSignalFlags()
    {
        TranslationWindow window = new(
            "@OPENING",
            2,
            [
                new WindowLine(1, "k1", "前一段的尾行", "@OPENING", false, true, 0, false, null),
                new WindowLine(2, "k2", "本段第一行", "@OPENING", true, false, 0, false, null),
                new WindowLine(3, "k3", "続きの行", "@OPENING", true, false, 2, false, null),
                new WindowLine(4, "k4", "\\@ X ? A # B \\@", "@OPENING", true, false, 0, true, "alt_001")
            ]);

        using JsonDocument document = JsonDocument.Parse(AiTranslationService.BuildWindowPayload(window));
        JsonElement root = document.RootElement;

        Assert.Equal(2, root.GetProperty("part").GetInt32());
        JsonElement[] lines = root.GetProperty("lines").EnumerateArray().ToArray();
        Assert.Equal(4, lines.Length);

        Assert.Equal(0, lines[0].GetProperty("do").GetInt32());
        Assert.Equal(1, lines[0].GetProperty("ref").GetInt32());

        Assert.Equal(1, lines[1].GetProperty("do").GetInt32());
        Assert.False(lines[1].TryGetProperty("join", out _));

        Assert.Equal(2, lines[2].GetProperty("join").GetInt32());

        Assert.Equal(1, lines[3].GetProperty("cond").GetInt32());
        Assert.Equal("alt_001", lines[3].GetProperty("alt").GetString());
        Assert.Equal("@OPENING", lines[3].GetProperty("fn").GetString());
    }

    [Fact]
    public void Parse_ReadsFlatObjectAndReportsMissingAndUnknown()
    {
        TranslationWindow window = Window(1, 2, 3, 4);

        WindowTranslationOutcome outcome = AiTranslationService.ParseWindowResponse(
            """{"1":"甲","2":"乙","99":"多余"}""",
            window);

        Assert.Equal(new[] { 1, 2 }, outcome.Translations.Select(item => item.Id));
        Assert.Equal(new[] { 3, 4 }, outcome.MissingIds);
        Assert.Equal(new[] { 99 }, outcome.UnknownIds);
        Assert.False(outcome.IsComplete);
    }

    [Fact]
    public void Parse_IgnoresEmptyTranslationsAndReferenceLineIds()
    {
        TranslationWindow window = Window(1, 2, 3, 4);

        WindowTranslationOutcome outcome = AiTranslationService.ParseWindowResponse(
            """{"1":"甲","2":"  "}""",
            window);

        Assert.Single(outcome.Translations);
        Assert.Contains(2, outcome.MissingIds);
    }

    [Fact]
    public void Parse_ToleratesCodeFenceAndSurroundingProse()
    {
        TranslationWindow window = Window(1);

        WindowTranslationOutcome outcome = AiTranslationService.ParseWindowResponse(
            "好的，结果如下：\n```json\n{\"1\":\"甲\"}\n```\n如需调整请告知。",
            window);

        AiTranslationResult result = Assert.Single(outcome.Translations);
        Assert.Equal("甲", result.Translation);
    }

    [Fact]
    public void Parse_StillAcceptsTheLegacyArrayShape()
    {
        TranslationWindow window = Window(1, 2);

        WindowTranslationOutcome outcome = AiTranslationService.ParseWindowResponse(
            """[{"id":1,"translation":"甲"},{"id":2,"translation":"乙"}]""",
            window);

        Assert.Equal(2, outcome.Translations.Count);
        Assert.True(outcome.IsComplete);
    }

    [Fact]
    public void Parse_ThrowsWhenThereIsNothingUsable()
    {
        Assert.Throws<InvalidDataException>(() =>
            AiTranslationService.ParseWindowResponse("抱歉，我无法翻译。", Window(1)));

        Assert.Throws<InvalidDataException>(() =>
            AiTranslationService.ParseWindowResponse("""{"1":""}""", Window(1)));
    }

    [Fact]
    public void Payload_EmitsChecksummedLineIds_AndParserResolvesThem()
    {
        TranslationWindow window = Window(1, 2);

        using JsonDocument document = JsonDocument.Parse(AiTranslationService.BuildWindowPayload(window));
        JsonElement[] lines = document.RootElement.GetProperty("lines").EnumerateArray().ToArray();
        string tag1 = lines[1].GetProperty("id").GetString()!;
        string tag2 = lines[2].GetProperty("id").GetString()!;

        Assert.Equal(AiTranslationService.FormatLineId(window.Lines[1]), tag1);
        Assert.Equal(AiTranslationService.FormatLineId(window.Lines[2]), tag2);
        Assert.StartsWith("1_", tag1, StringComparison.Ordinal);
        Assert.StartsWith("2_", tag2, StringComparison.Ordinal);

        string response = $"{{\"{tag1}\":\"甲\",\"{tag2}\":\"乙\"}}";
        WindowTranslationOutcome outcome = AiTranslationService.ParseWindowResponse(response, window);

        Assert.True(outcome.IsComplete);
        Assert.Equal(new[] { (1, "甲"), (2, "乙") }, outcome.Translations.Select(t => (t.Id, t.Translation)));
    }

    [Fact]
    public void Parse_RejectsMismatchedChecksumTag()
    {
        TranslationWindow window = Window(1, 2);
        string tag1 = AiTranslationService.FormatLineId(window.Lines[1]);
        string badTag2 = "2_zz";

        WindowTranslationOutcome outcome = AiTranslationService.ParseWindowResponse(
            $"{{\"{tag1}\":\"甲\",\"{badTag2}\":\"错位译文\"}}",
            window);

        Assert.Single(outcome.Translations);
        Assert.Equal(1, outcome.Translations[0].Id);
        Assert.Equal(new[] { 2 }, outcome.MissingIds);
        Assert.Equal(new[] { 2 }, outcome.UnknownIds);
    }

    [Fact]
    public void Parse_RescuesIntactEntriesFromTruncatedOrMalformedJson()
    {
        TranslationWindow window = Window(1, 2, 3, 4);

        // Truncated before closing brace
        WindowTranslationOutcome truncated = AiTranslationService.ParseWindowResponse(
            "{\"1\":\"甲\",\"2\":\"乙\",\"3\":\"被截断的半句",
            window);
        Assert.Equal(new[] { 1, 2 }, truncated.Translations.Select(t => t.Id));
        Assert.Equal(new[] { 3, 4 }, truncated.MissingIds);

        // Malformed unescaped quotes on entry 2, valid entries before and after
        WindowTranslationOutcome malformed = AiTranslationService.ParseWindowResponse(
            "{\"1\":\"甲\",\"2\":\"他说\"坏引号\"了\",\"3\":\"丙\",\"4\":\"丁\"}",
            window);
        Assert.Equal(new[] { 1, 3, 4 }, malformed.Translations.Select(t => t.Id));
        Assert.Equal(new[] { 2 }, malformed.MissingIds);
    }

    [Fact]
    public void Parse_DetectsPlaceholderMismatchAndCascadingIdShift()
    {
        var window = new TranslationWindow(
            "TRAIN_MESSAGE_A",
            1,
            [
                new WindowLine(0, "k0", "[[P0]]は身体をよじる。", "TRAIN_MESSAGE_A", true, false, 0, false, null),
                new WindowLine(1, "k1", "[[P0]]は反抗的だ。", "TRAIN_MESSAGE_A", true, false, 0, false, null),
                new WindowLine(2, "k2", "触手の蠢き", "TRAIN_MESSAGE_A", true, false, 1, false, "alt_002"),
                new WindowLine(3, "k3", "に反応して震えている。", "TRAIN_MESSAGE_A", true, false, 1, false, "alt_001"),
                new WindowLine(4, "k4", "[[P0]]は小さく震えた。", "TRAIN_MESSAGE_A", true, false, 0, false, null)
            ]);

        // Model merged lines and shifted ids starting at 2:
        // id 2 (no placeholder) got [[P0]], id 3 (no placeholder) got [[P0]], id 4 got no placeholder.
        string shiftedReply =
            """{"0":"[[P0]]扭动着身体。","1":"[[P0]]态度反抗。","2":"[[P0]]因触手颤抖。","3":"[[P0]]微微颤抖。","4":"完全错位的尾巴。"}""";

        WindowTranslationOutcome outcome = AiTranslationService.ParseWindowResponse(shiftedReply, window);

        Assert.Equal(new[] { 0, 1 }, outcome.Translations.Select(t => t.Id));
        Assert.Equal(new[] { 2, 3, 4 }, outcome.MissingIds);
    }

    private static TranslationWindow Window(params int[] translateIds)
    {
        var lines = new List<WindowLine>();
        for (int index = 0; index < translateIds.Length; index++)
        {
            lines.Add(new WindowLine(
                translateIds[index],
                "k" + translateIds[index],
                "原文" + translateIds[index],
                "@A",
                true,
                false,
                0,
                false,
                null));
        }

        if (translateIds.Length > 0)
        {
            lines.Insert(0, new WindowLine(
                translateIds[0] - 1,
                "ref",
                "上一段尾行",
                "@A",
                false,
                true,
                0,
                false,
                null));
        }

        return new TranslationWindow("@A", 1, lines);
    }
}
