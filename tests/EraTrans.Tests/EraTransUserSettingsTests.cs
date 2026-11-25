using EraTrans.Core;
using EraTrans.Core.VisibleText;
using System.Text;
using Xunit;

namespace EraTrans.Tests;

public sealed class EraTransUserSettingsTests
{
    [Fact]
    public void DefaultConfigurationPaths_AreBesideApplicationInEnglishConfigFolder()
    {
        Assert.Equal(
            Path.Combine(AppContext.BaseDirectory, "Config", "settings.json"),
            EraTransPaths.UserSettingsFilePath);
        Assert.Equal(
            Path.Combine(AppContext.BaseDirectory, "Config", "ai-providers.json"),
            EraTransPaths.AiProviderSettingsFilePath);
        Assert.Equal(
            Path.Combine(AppContext.BaseDirectory, "Config", "ai-translation-logs"),
            EraTransPaths.AiTranslationLogDirectoryPath);
    }

    [Fact]
    public void CreateDefault_MatchesOriginalParserFirstRunSettings()
    {
        EraTransUserSettings settings = EraTransUserSettings.CreateDefault();

        Assert.Equal("932", settings.FileEncodingId);
        Assert.Equal("lexer", settings.ErbExtractionMode);
        Assert.Equal(ExtractionMode.Lexer, settings.ToParserOptions().ExtractionMode);
        Assert.True(settings.AutoOpenFolder);
        Assert.False(settings.EnableVariableAutoCorrection);
        Assert.True(settings.HideEnglishText);
        Assert.False(settings.MergeDuplicateEntries);
        Assert.True(settings.HideVariableEntries);
        Assert.True(settings.MergeConcatenatedStrings);
        Assert.True(settings.WriteContext);
        Assert.True(settings.ExtractHtmlTitle);
        Assert.True(settings.ExtractPrintDataEntries);
        Assert.True(settings.SkipNonJapaneseEntries);
        Assert.True(settings.EnableErbSemanticGroupingTranslation);
        Assert.Equal(string.Empty, settings.LastGameDirectory);
        Assert.Equal(string.Empty, settings.LastOutputRootDirectory);
        Assert.Equal(string.Empty, settings.LastDictionaryFileRelativePath);
        Assert.True(new EraParserOptions().HideVariableEntries);
        Assert.Equal(new[] { ".csv", ".erb", ".erh", ".erd" }, settings.TargetFileExtensions);
        Assert.Equal(
            new[]
            {
                "Abl", "Base", "CFlag", "CSTR", "Equip", "Ex", "Exp", "Flag",
                "Global", "Item", "Juel", "Mark", "Nowex", "Palam", "Source",
                "Stain", "Talent", "TCVAR", "TEquip", "Tflag", "Train", "TSTR", "修正字典"
            },
            settings.AutoReplaceReferenceFiles);
        Assert.Equal(new[] { "☆", "♡", "∀", "←", "→" }, settings.VariableNameCharacters);
        Assert.Contains("TO", settings.Operators);
    }

    [Fact]
    public void Store_SaveAndLoad_RoundTripsCurrentParserSettings()
    {
        using var temporary = new TemporaryDirectory();
        var store = new EraTransUserSettingsStore(Path.Combine(temporary.Path, "settings.json"));
        var settings = EraTransUserSettings.CreateDefault();
        settings.FileEncodingId = "932";
        settings.AutoOpenFolder = false;
        settings.EnableVariableAutoCorrection = true;
        settings.HideEnglishText = false;
        settings.MergeDuplicateEntries = true;
        settings.HideVariableEntries = false;
        settings.MergeConcatenatedStrings = false;
        settings.WriteContext = false;
        settings.EnableErbSemanticGroupingTranslation = false;
        settings.LastGameDirectory = @"D:\Games\Sample";
        settings.LastOutputRootDirectory = @"E:\EraTransOutput";
        settings.LastDictionaryFileRelativePath = @"ERB\EVENT.JSON";
        settings.TargetFileExtensions = [".erb", ".erh"];
        settings.AutoReplaceReferenceFiles = ["Abl", "Base"];
        settings.VariableNameCharacters = ["☆", "※"];
        settings.Operators = ["(", ")", "=", "=="];

        store.Save(settings);
        EraTransUserSettings loaded = store.Load();

        Assert.Equal(settings.FileEncodingId, loaded.FileEncodingId);
        Assert.Equal(settings.AutoOpenFolder, loaded.AutoOpenFolder);
        Assert.Equal(settings.EnableVariableAutoCorrection, loaded.EnableVariableAutoCorrection);
        Assert.Equal(settings.HideEnglishText, loaded.HideEnglishText);
        Assert.Equal(settings.MergeDuplicateEntries, loaded.MergeDuplicateEntries);
        Assert.False(loaded.HideVariableEntries);
        Assert.Equal(settings.MergeConcatenatedStrings, loaded.MergeConcatenatedStrings);
        Assert.Equal(settings.WriteContext, loaded.WriteContext);
        Assert.Equal(settings.EnableErbSemanticGroupingTranslation, loaded.EnableErbSemanticGroupingTranslation);
        Assert.Equal(settings.LastGameDirectory, loaded.LastGameDirectory);
        Assert.Equal(settings.LastOutputRootDirectory, loaded.LastOutputRootDirectory);
        Assert.Equal(settings.LastDictionaryFileRelativePath, loaded.LastDictionaryFileRelativePath);
        Assert.Equal(settings.TargetFileExtensions, loaded.TargetFileExtensions);
        Assert.Equal(settings.AutoReplaceReferenceFiles, loaded.AutoReplaceReferenceFiles);
        Assert.Equal(settings.VariableNameCharacters, loaded.VariableNameCharacters);
        Assert.Equal(settings.Operators, loaded.Operators);
        Assert.Equal(932, loaded.ToParserOptions().FileEncoding.CodePage);
    }

    [Fact]
    public void Store_LoadLegacySettingsWithoutWorkspaceFields_UsesEmptyWorkspaceDefaults()
    {
        using var temporary = new TemporaryDirectory();
        string path = Path.Combine(temporary.Path, "settings.json");
        File.WriteAllText(path, """{"FileEncodingId":"utf8-bom","FileEncodingOverrides":{"ERB/old.erb":"950"}}""");

        EraTransUserSettings loaded = new EraTransUserSettingsStore(path).Load();

        Assert.Equal(string.Empty, loaded.LastGameDirectory);
        Assert.Equal(string.Empty, loaded.LastOutputRootDirectory);
        Assert.Equal(string.Empty, loaded.LastDictionaryFileRelativePath);
        Assert.Equal("932", loaded.FileEncodingId);
    }

    [Fact]
    public void Store_LoadMissingFile_UsesCurrentDefaultsWithoutCreatingAFile()
    {
        using var temporary = new TemporaryDirectory();
        string path = Path.Combine(temporary.Path, "settings.json");

        EraTransUserSettings settings = new EraTransUserSettingsStore(path).Load();

        Assert.False(File.Exists(path));
        Assert.Equal("lexer", settings.ErbExtractionMode);
        Assert.True(settings.EnableErbSemanticGroupingTranslation);
        Assert.True(settings.HideVariableEntries);
    }

    [Fact]
    public void ToParserOptions_MapsParserBehaviorAndAdvancedLists()
    {
        EraTransUserSettings settings = EraTransUserSettings.CreateDefault();
        settings.FileEncodingId = "936";
        settings.HideEnglishText = false;
        settings.MergeDuplicateEntries = true;
        settings.HideVariableEntries = true;
        settings.MergeConcatenatedStrings = false;
        settings.WriteContext = false;
        settings.EnableVariableAutoCorrection = false;
        settings.TargetFileExtensions = [".erb"];
        settings.AutoReplaceReferenceFiles = ["Base"];
        settings.VariableNameCharacters = ["※"];
        settings.Operators = ["(", ")", "=="];

        EraParserOptions options = settings.ToParserOptions();

        Assert.Equal(936, options.FileEncoding.CodePage);
        Assert.False(options.HideEnglishText);
        Assert.True(options.MergeDuplicateEntries);
        Assert.True(options.HideVariableEntries);
        Assert.False(options.MergeConcatenatedStrings);
        Assert.False(options.WriteContext);
        Assert.False(options.EnableVariableAutoCorrection);
        Assert.True(options.ExtractHtmlTitle);
        Assert.True(options.ExtractPrintDataEntries);
        Assert.True(options.SkipNonJapaneseEntries);
        Assert.Equal(new[] { ".erb" }, options.TargetFileExtensions);
        Assert.Equal(new[] { "Base" }, options.AutoReplaceReferenceFiles);
        Assert.Equal(new[] { "※" }, options.VariableNameCharacters);
        Assert.Equal(new[] { "(", ")", "==" }, options.Operators);
    }


    [Theory]
    [InlineData("utf8-bom")]
    [InlineData("utf8")]
    public void Validate_MigratesLegacyUtf8FallbackSettingsToJapaneseCp932(string legacyEncodingId)
    {
        EraTransUserSettings settings = EraTransUserSettings.CreateDefault();
        settings.FileEncodingId = legacyEncodingId;

        EraParserOptions options = settings.ToParserOptions();

        Assert.Equal("932", settings.FileEncodingId);
        Assert.Equal(932, options.FileEncoding.CodePage);
    }

    [Fact]
    public void Validate_RejectsInvalidExtensionAndUnsafeReferencePath()
    {
        EraTransUserSettings extensionSettings = EraTransUserSettings.CreateDefault();
        extensionSettings.TargetFileExtensions = ["../*.erb"];
        Assert.Throws<InvalidDataException>(() => extensionSettings.Validate());

        EraTransUserSettings referenceSettings = EraTransUserSettings.CreateDefault();
        referenceSettings.AutoReplaceReferenceFiles = ["../Abl"];
        Assert.Throws<InvalidDataException>(() => referenceSettings.Validate());
    }


    [Fact]
    public void Defaults_UseShiftJisForOriginalJapaneseEraGames()
    {
        EraTransUserSettings settings = EraTransUserSettings.CreateDefault();
        Assert.Equal("932", settings.FileEncodingId);
        Assert.Equal(932, settings.ToParserOptions().FileEncoding.CodePage);
    }

    [Fact]
    public void Store_LoadInvalidJson_ReportsCorruptedSettingsWithoutReplacingFile()
    {
        using var temporary = new TemporaryDirectory();
        string path = Path.Combine(temporary.Path, "settings.json");
        const string invalidJson = "{ this is not json";
        File.WriteAllText(path, invalidJson);

        Assert.Throws<InvalidDataException>(() => new EraTransUserSettingsStore(path).Load());
        Assert.Equal(invalidJson, File.ReadAllText(path));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "EraTransSettingsTests",
            Guid.NewGuid().ToString("N"));

        public TemporaryDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
