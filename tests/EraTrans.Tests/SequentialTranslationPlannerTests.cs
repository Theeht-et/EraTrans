using EraTrans.Core;
using Xunit;

namespace EraTrans.Tests;

public sealed class SequentialTranslationPlannerTests
{
    [Fact]
    public void SmallFunctions_ShareOneWindow_WithoutCuttingAFunction()
    {
        var sources = new List<WindowSourceLine>
        {
            new(1, "a1", new string('あ', 100), true),
            new(2, "a2", new string('い', 100), true),
            new(3, "b1", new string('う', 100), true)
        };

        WindowPlan plan = new SequentialTranslationPlanner().Build(
            sources,
            Index(
                Link("a1", 0, "@A"),
                Link("a2", 1, "@A"),
                Link("b1", 2, "@B")));

        TranslationWindow window = Assert.Single(plan.Windows);
        Assert.Equal(new[] { 1, 2, 3 }, window.Lines.Select(line => line.Id));
        Assert.Equal(1, window.Part);
        Assert.Equal(2, plan.FunctionCount);
        Assert.Equal(0, plan.SplitFunctionCount);
    }

    [Fact]
    public void FunctionsThatDoNotFitTogether_GetTheirOwnWindows_StillUnsplitted()
    {
        var sources = new List<WindowSourceLine>
        {
            new(1, "a1", new string('あ', 600), true),
            new(2, "b1", new string('い', 600), true)
        };

        WindowPlan plan = new SequentialTranslationPlanner().Build(
            sources,
            Index(Link("a1", 0, "@A"), Link("b1", 1, "@B")),
            maxCharacters: 1000);

        Assert.Equal(2, plan.Windows.Count);
        Assert.All(plan.Windows, window => Assert.Single(window.Lines));
        Assert.Equal(0, plan.SplitFunctionCount);
    }

    [Fact]
    public void OversizedFunction_IsSplitAndNumbered()
    {
        var sources = new List<WindowSourceLine>();
        var links = new List<ErbTranslationGroupEntry>();
        for (int index = 0; index < 8; index++)
        {
            string key = "k" + index;
            sources.Add(new WindowSourceLine(index, key, new string('あ', 300), true));
            links.Add(Link(
                key,
                index,
                "@A",
                branch: index < 4 ? "F:IF:0" : "F:IF:1",
                label: index < 4 ? "IF" : "ELSE"));
        }

        WindowPlan plan = new SequentialTranslationPlanner().Build(
            sources,
            Index([.. links]),
            maxCharacters: 1000);

        Assert.Equal(1, plan.SplitFunctionCount);
        Assert.True(plan.Windows.Count > 1);
        Assert.Equal(
            Enumerable.Range(1, plan.Windows.Count),
            plan.Windows.Select(window => window.Part));
        Assert.All(plan.Windows, window => Assert.Equal("@A", window.FunctionId));

        // No window may contain lines from more than one side of the branch.
        foreach (TranslationWindow window in plan.Windows)
        {
            string[] branches = window.Lines
                .Where(line => !line.Reference)
                .Select(line => links.First(link => link.Key == line.Key).BranchPath)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            Assert.Single(branches);
        }
    }

    [Fact]
    public void SplitPart_StartsWithReferenceLinesFromThePreviousPart()
    {
        var sources = new List<WindowSourceLine>();
        var links = new List<ErbTranslationGroupEntry>();
        for (int index = 0; index < 10; index++)
        {
            string key = "k" + index;
            sources.Add(new WindowSourceLine(index, key, new string('あ', 300), true));
            links.Add(Link(key, index, "@A"));
        }

        WindowPlan plan = new SequentialTranslationPlanner().Build(
            sources,
            Index([.. links]),
            maxCharacters: 1000,
            overlapLines: 2);

        WindowLine[] references = plan.Windows[1].Lines.Where(line => line.Reference).ToArray();
        Assert.Equal(2, references.Length);
        Assert.All(references, line => Assert.False(line.Translate));
        Assert.Equal(
            plan.Windows[0].Lines.TakeLast(2).Select(line => line.Id),
            references.Select(line => line.Id));
    }

    [Fact]
    public void JoinSignal_MarksSameRenderedLine_AndLikelyContinuation()
    {
        var sources = new List<WindowSourceLine>
        {
            new(1, "k1", "そう思った、%CALLNAME:MASTER%は", true),
            new(2, "k2", "嫁である", true),
            new(3, "k3", "「う～ん、そうですね～", true),
            new(4, "k4", "\u3000外から人を引っ張る", true),
            new(5, "k5", "終わり。", true),
            new(6, "k6", "次の行。", true)
        };

        WindowPlan plan = new SequentialTranslationPlanner().Build(
            sources,
            Index(
                Link("k1", 0, "@A", outputType: "PRINTFORM"),
                Link("k2", 1, "@A", outputType: "PRINTFORMW"),
                Link("k3", 2, "@A"),
                Link("k4", 3, "@A"),
                Link("k5", 4, "@A"),
                Link("k6", 5, "@A")));

        WindowLine[] lines = Assert.Single(plan.Windows).Lines.ToArray();
        Assert.Equal(0, lines[0].Join);
        Assert.Equal(1, lines[1].Join);   // previous PRINTFORM renders without a break
        Assert.Equal(2, lines[2].Join);   // previous line has an unclosed quote
        Assert.Equal(2, lines[3].Join);   // ideographic-space indent
        Assert.Equal(2, lines[4].Join);   // previous line lacks sentence-final punctuation
        Assert.Equal(0, lines[5].Join);
        Assert.Equal(1, plan.JoinedLineCount);
        Assert.Equal(3, lines.Count(line => line.Join == 2));
    }

    [Fact]
    public void InlineCondition_IsFlaggedPerLine()
    {
        var sources = new List<WindowSourceLine>
        {
            new(1, "k1", "普通の行。", true),
            new(2, "k2", "怒りながら\\@ FLAG:X ? 帰った # 去った \\@", true)
        };

        WindowPlan plan = new SequentialTranslationPlanner().Build(
            sources,
            Index(Link("k1", 0, "@A"), Link("k2", 1, "@A")));

        WindowLine[] lines = Assert.Single(plan.Windows).Lines.ToArray();
        Assert.False(lines[0].InlineCondition);
        Assert.True(lines[1].InlineCondition);
        Assert.Equal(1, plan.InlineConditionCount);
    }

    [Fact]
    public void AlternativeBranches_ShareOneGroupOnlyWhenThereIsMoreThanOne()
    {
        var sources = new List<WindowSourceLine>
        {
            new(1, "k1", "甲。", true),
            new(2, "k2", "乙。", true),
            new(3, "k3", "丙。", true)
        };

        WindowPlan plan = new SequentialTranslationPlanner().Build(
            sources,
            Index(
                Link("k1", 0, "@A", branch: "F:SELECTCASE:CASE 1", label: "CASE1"),
                Link("k2", 1, "@A", branch: "F:SELECTCASE:CASE 2", label: "CASE2"),
                Link("k3", 2, "@A", branch: "F:SELECTCASE:CASE 2", label: "CASE2")));

        WindowLine[] lines = Assert.Single(plan.Windows).Lines.ToArray();
        Assert.NotNull(lines[0].AltGroup);
        Assert.Equal(lines[0].AltGroup, lines[1].AltGroup);
        Assert.Equal(lines[0].AltGroup, lines[2].AltGroup);
        Assert.Equal(3, plan.AlternativeLineCount);
    }

    [Fact]
    public void LinesWithoutSidecarLink_AreSkipped()
    {
        var sources = new List<WindowSourceLine>
        {
            new(1, "k1", "有的。", true),
            new(2, "missing", "没分组信息。", true)
        };

        WindowPlan plan = new SequentialTranslationPlanner().Build(sources, Index(Link("k1", 0, "@A")));

        WindowLine line = Assert.Single(Assert.Single(plan.Windows).Lines);
        Assert.Equal("k1", line.Key);
    }

    [Fact]
    public void EmptyDictionary_ProducesNoWindows()
    {
        WindowPlan plan = new SequentialTranslationPlanner().Build([], Index());
        Assert.Empty(plan.Windows);
        Assert.Equal(0, plan.TargetLineCount);
    }

    private static ErbTranslationGroupingIndex Index(params ErbTranslationGroupEntry[] entries) =>
        new(1, "a.ERB", "source-fingerprint", "a.json", "dictionary-fingerprint", [], entries);

    [Fact]
    public void PrintFormCommand_ProducesTheJoinSignal()
    {
        var sources = new List<WindowSourceLine>
        {
            new(1, "k1", "こんにちは、", true),
            new(2, "k2", "いい天気ですね", true)
        };

        WindowPlan plan = new SequentialTranslationPlanner().Build(
            sources,
            Index(
                Link("k1", 0, "@A", command: "PRINTFORM"),
                Link("k2", 1, "@A", command: "PRINTL")));

        TranslationWindow window = Assert.Single(plan.Windows);
        Assert.Equal(1, window.Lines[1].Join);
        Assert.Equal("PRINTFORM", window.Lines[0].Command);
        Assert.Equal(1, plan.JoinedLineCount);
    }

    [Fact]
    public void Window_CarriesSceneIdAndGroupType()
    {
        var sources = new List<WindowSourceLine>
        {
            new(1, "k1", "こんにちは", true),
            new(2, "k2", "おはよう", true)
        };

        WindowPlan plan = new SequentialTranslationPlanner().Build(
            sources,
            Indexed(
                "menu",
                Link("k1", 0, "@A", scope: "A#0000"),
                Link("k2", 1, "@A", scope: "A#0000")));

        TranslationWindow window = Assert.Single(plan.Windows);
        Assert.Equal("A#0000", window.SceneId);
        Assert.Equal("menu", window.SceneType);
        Assert.All(window.Lines, line => Assert.True(line.Menu));
    }

    private static ErbTranslationGroupingIndex Indexed(
        string groupType,
        params ErbTranslationGroupEntry[] entries) =>
        new(
            1,
            "a.ERB",
            "source-fingerprint",
            "a.json",
            "dictionary-fingerprint",
            [
                new ErbTranslationGroup(
                    "group_000001",
                    groupType,
                    entries[0].FunctionId,
                    entries[0].BranchPath,
                    entries[0].ControlScopeId,
                    entries[0].BranchLabel,
                    entries[0].Sequence,
                    entries[^1].Sequence,
                    [.. entries.Select(entry => entry.Key)])
            ],
            entries);

    private static ErbTranslationGroupEntry Link(
        string key,
        int sequence,
        string functionId,
        string branch = "",
        string scope = "",
        string label = "",
        string outputType = "PRINTFORMW",
        string? command = null,
        string? kind = null) =>
        new(key, "原文" + key, sequence, sequence, functionId, outputType, branch, scope, label,
            "group_000001", "dialogue", null, command, kind);
}
