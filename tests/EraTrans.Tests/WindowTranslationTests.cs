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
