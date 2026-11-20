using System.Text;

namespace EraTrans.Core;

/// <summary>
/// User-configurable application and legacy parser settings.
/// The property defaults mirror the original ErbParserForTranslate first-run config.
/// </summary>
public sealed class EraTransUserSettings
{
    public string FileEncodingId { get; set; } = "utf8-bom";
    public bool ForceFilterVariableNames { get; set; } = true;
    public bool AutoOpenFolder { get; set; } = true;
    public bool EnableVariableAutoCorrection { get; set; } = true;
    public bool HideEnglishText { get; set; } = true;
    public bool MergeDuplicateEntries { get; set; } = false;
    public bool HideVariableEntries { get; set; } = true;
    public bool MergeConcatenatedStrings { get; set; } = true;
    public bool WriteContext { get; set; } = true;
    public bool EnableErbSemanticGroupingTranslation { get; set; } = false;

    public List<string> TargetFileExtensions { get; set; } =
    [
        ".csv", ".erb", ".erh", ".erd"
    ];

    public List<string> AutoReplaceReferenceFiles { get; set; } =
    [
        "Abl", "Base", "CFlag", "CSTR", "Equip", "Ex",
        "Exp", "Flag", "Global", "Item", "Juel",
        "Mark", "Nowex", "Palam", "Source", "Stain",
        "Talent", "TCVAR", "TEquip", "Tflag", "Train",
        "TSTR", "修正字典"
    ];

    public List<string> VariableNameCharacters { get; set; } =
    [
        "☆", "♡", "∀", "←", "→"
    ];

    public List<string> Operators { get; set; } =
    [
        "(", ")", "{", "}", "?", "#", "=", "+=", "-=", "*=", "/=",
        ">", "<", "==", "!=", ">=", "<=", "&&", "||", "&", "|", "!",
        "+", "-", "*", "/", "%", "++", "--", ",", ":", "TO"
    ];

    public static EraTransUserSettings CreateDefault() => new();

    public Encoding CreateFileEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return FileEncodingId switch
        {
            "utf8-bom" => new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
            "utf8" => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            "932" => Encoding.GetEncoding(932),
            "936" => Encoding.GetEncoding(936),
            "950" => Encoding.GetEncoding(950),
            _ => throw new InvalidDataException($"不支持的文件编码设置：{FileEncodingId}")
        };
    }

    public EraParserOptions ToParserOptions()
    {
        Validate();

        return new EraParserOptions
        {
            FileEncoding = CreateFileEncoding(),
            ForceFilterVariableNames = ForceFilterVariableNames,
            HideEnglishText = HideEnglishText,
            MergeDuplicateEntries = MergeDuplicateEntries,
            HideVariableEntries = HideVariableEntries,
            MergeConcatenatedStrings = MergeConcatenatedStrings,
            WriteContext = WriteContext,
            EnableVariableAutoCorrection = EnableVariableAutoCorrection,
            AutoReplaceReferenceFiles = AutoReplaceReferenceFiles.ToArray(),
            TargetFileExtensions = TargetFileExtensions.ToArray(),
            VariableNameCharacters = VariableNameCharacters.ToArray(),
            Operators = Operators.ToArray()
        };
    }

    public void Validate()
    {
        if (FileEncodingId is not ("utf8-bom" or "utf8" or "932" or "936" or "950"))
        {
            throw new InvalidDataException($"不支持的文件编码设置：{FileEncodingId}");
        }

        ValidateList(TargetFileExtensions, "游戏文件扩展名", value =>
            value.StartsWith(".", StringComparison.Ordinal) &&
            value.Length > 1 &&
            value.IndexOfAny(['/', '\\', '*', '?', ':']) < 0);

        ValidateList(AutoReplaceReferenceFiles, "变量自动修正参考文件", value =>
            !Path.IsPathRooted(value) &&
            Path.GetFileName(value).Equals(value, StringComparison.Ordinal) &&
            !value.Contains('/') &&
            !value.Contains('\\') &&
            !value.EndsWith(".json", StringComparison.OrdinalIgnoreCase));

        ValidateList(VariableNameCharacters, "变量名字符", value => value.Length == 1);
        ValidateList(Operators, "操作符", value => !value.Any(char.IsWhiteSpace));
    }

    private static void ValidateList(
        List<string>? values,
        string displayName,
        Func<string, bool> predicate)
    {
        if (values is null || values.Count == 0)
        {
            throw new InvalidDataException($"{displayName}列表不能为空，且不能包含无效项目。");
        }

        for (int index = 0; index < values.Count; index++)
        {
            string? value = values[index];
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidDataException($"{displayName}列表不能为空，且不能包含无效项目。");
            }

            string normalizedValue = value.Trim();
            if (!predicate(normalizedValue))
            {
                throw new InvalidDataException($"{displayName}列表不能为空，且不能包含无效项目。");
            }

            values[index] = normalizedValue;
        }

        if (values.Distinct(StringComparer.OrdinalIgnoreCase).Count() != values.Count)
        {
            throw new InvalidDataException($"{displayName}列表包含重复项目。");
        }
    }
}
