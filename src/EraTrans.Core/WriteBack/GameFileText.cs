using System.Text;
using System.Text.RegularExpressions;

namespace EraTrans.Core.WriteBack;

/// <summary>
/// Reads and writes game text files without silently guessing between overlapping encodings.
/// BOM and XML declarations are explicit signals; BOM-less text must have a unique lossless
/// interpretation as strict UTF-8 or the selected game code page.
/// </summary>
public sealed class GameFileText
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private static readonly Encoding StrictUtf16Le = new UnicodeEncoding(false, false, true);
    private static readonly Encoding StrictUtf16Be = new UnicodeEncoding(true, false, true);
    private static readonly Regex XmlEncodingDeclaration = new(
        @"^\s*<\?xml\b[^>]*\bencoding\s*=\s*(['""])(?<name>[^'""]+)\1",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly byte[] _body;

    private GameFileText(string text, Encoding encoding, byte[] preamble, byte[] body)
    {
        Text = text;
        Encoding = encoding;
        Preamble = preamble;
        _body = body;
    }

    public string Text { get; set; }
    public Encoding Encoding { get; }
    public byte[] Preamble { get; }
    public bool HasBom => Preamble.Length > 0;

    public static GameFileText Read(string path) => Read(path, null);

    public static GameFileText Read(string path, Encoding? fallbackEncoding)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        byte[] bytes = File.ReadAllBytes(path);
        try
        {
            Encoding? declaredEncoding = GetXmlDeclaredEncoding(path, bytes);
            DetectedEncoding detected = DetectEncoding(bytes, fallbackEncoding, declaredEncoding);
            string text = detected.Encoding.GetString(bytes, detected.BodyOffset, bytes.Length - detected.BodyOffset);
            byte[] body = bytes.AsSpan(detected.BodyOffset).ToArray();
            return new GameFileText(text, detected.Encoding, detected.Preamble, body);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException($"文本文件编码检测失败：{path}\n{ex.Message}", ex);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException($"文本文件无法按识别出的编码无损解码：{path}", ex);
        }
    }

    public void Write(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        byte[] body = Encoding.GetBytes(Text);
        if (Preamble.Length == 0)
        {
            File.WriteAllBytes(path, body);
            return;
        }
        byte[] result = new byte[Preamble.Length + body.Length];
        Buffer.BlockCopy(Preamble, 0, result, 0, Preamble.Length);
        Buffer.BlockCopy(body, 0, result, Preamble.Length, body.Length);
        File.WriteAllBytes(path, result);
    }

    public bool TryWriteSpliced(string path, IReadOnlyList<SpanEdit> edits, out string? warning)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(edits);
        warning = null;

        string text = Text;
        int charCursor = 0;
        int sourceCursor = 0;
        using var stream = new MemoryStream(_body.Length + 64);

        foreach (SpanEdit edit in edits)
        {
            int segmentChars = edit.Span.Start - charCursor;
            int segmentBytes = Encoding.GetByteCount(text.AsSpan(charCursor, segmentChars));
            stream.Write(_body, sourceCursor, segmentBytes);
            sourceCursor += segmentBytes;
            stream.Write(Encoding.GetBytes(edit.Replacement));
            sourceCursor += Encoding.GetByteCount(text.AsSpan(edit.Span.Start, edit.Span.Length));
            charCursor = edit.Span.End;
        }

        int tailBytes = Encoding.GetByteCount(text.AsSpan(charCursor));
        if (sourceCursor + tailBytes != _body.Length)
        {
            warning = "源文件字节与解码文本不一致，已改用整文件重编码写回。";
            return false;
        }
        stream.Write(_body, sourceCursor, tailBytes);
        byte[] result = new byte[Preamble.Length + stream.Length];
        Buffer.BlockCopy(Preamble, 0, result, 0, Preamble.Length);
        stream.Position = 0;
        stream.ReadExactly(result.AsSpan(Preamble.Length));
        File.WriteAllBytes(path, result);
        return true;
    }

    public static DetectedEncoding DetectEncoding(
        ReadOnlySpan<byte> bytes,
        Encoding? fallbackEncoding = null,
        Encoding? declaredEncoding = null)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return new DetectedEncoding(StrictUtf8, [0xEF, 0xBB, 0xBF], 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return new DetectedEncoding(StrictUtf16Le, [0xFF, 0xFE], 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return new DetectedEncoding(StrictUtf16Be, [0xFE, 0xFF], 2);

        if (declaredEncoding is not null)
        {
            Encoding strictDeclared = CreateStrictEncoding(declaredEncoding);
            if (!TryDecode(bytes, strictDeclared, out _))
                throw new InvalidDataException($"XML 文件声明的编码 {declaredEncoding.WebName} 无法无损解码文件内容。");
            return new DetectedEncoding(strictDeclared, [], 0);
        }

        if (TryDetectBomlessUtf16(bytes, out DetectedEncoding utf16))
            return utf16;

        if (TryDecode(bytes, StrictUtf8, out string utf8Text))
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            Encoding configured = fallbackEncoding ?? Encoding.GetEncoding(932);
            Encoding strictConfigured = CreateStrictEncoding(configured);
            if (configured.CodePage == StrictUtf8.CodePage)
                return new DetectedEncoding(StrictUtf8, [], 0);

            if (TryDecode(bytes, strictConfigured, out string legacyText) &&
                !string.Equals(utf8Text, legacyText, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"无 BOM 文件同时符合严格 UTF-8 和 CP{configured.CodePage}，但解码结果不同，无法可靠判断原始编码。为避免静默乱码，请先用文本编辑器确认编码，并将该文件转换为 UTF-8 BOM 后重试。");
            }
            return new DetectedEncoding(StrictUtf8, [], 0);
        }

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding selectedFallback = CreateStrictEncoding(fallbackEncoding ?? Encoding.GetEncoding(932));
        if (TryDecode(bytes, selectedFallback, out _))
            return new DetectedEncoding(selectedFallback, [], 0);

        throw new InvalidDataException(
            $"文件既不是有效的严格 UTF-8，也无法按所选游戏编码 CP{selectedFallback.CodePage} 无损解码。请在文本编辑器中确认编码，并转换为 UTF-8 BOM 后重试。");
    }

    private static Encoding? GetXmlDeclaredEncoding(string path, ReadOnlySpan<byte> bytes)
    {
        if (!Path.GetExtension(path).Equals(".xml", StringComparison.OrdinalIgnoreCase))
            return null;
        if ((bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) ||
            (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) ||
            (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF))
            return null;

        int headerLength = Math.Min(bytes.Length, 1024);
        string header = System.Text.Encoding.ASCII.GetString(bytes[..headerLength]);
        Match match = XmlEncodingDeclaration.Match(header);
        if (!match.Success)
            return null;

        string encodingName = match.Groups["name"].Value.Trim();
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(encodingName, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException($"XML 文件声明了不支持的编码“{encodingName}”。", ex);
        }
    }

    private static Encoding CreateStrictEncoding(Encoding encoding) =>
        Encoding.GetEncoding(encoding.CodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);

    private static bool TryDecode(ReadOnlySpan<byte> bytes, Encoding encoding, out string text)
    {
        try
        {
            text = encoding.GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            text = string.Empty;
            return false;
        }
    }

    private static bool TryDetectBomlessUtf16(ReadOnlySpan<byte> bytes, out DetectedEncoding detected)
    {
        detected = default;
        if (bytes.Length < 16 || bytes.Length % 2 != 0)
            return false;
        int evenZeros = 0, oddZeros = 0;
        for (int i = 0; i < bytes.Length; i += 2)
        {
            if (bytes[i] == 0) evenZeros++;
            if (bytes[i + 1] == 0) oddZeros++;
        }
        int pairs = bytes.Length / 2;
        int laneThreshold = pairs / 4;
        int otherThreshold = Math.Max(1, pairs / 50);

        if (oddZeros >= laneThreshold && evenZeros <= otherThreshold && TryDecodeCleanUtf16(bytes, StrictUtf16Le))
        {
            detected = new DetectedEncoding(StrictUtf16Le, [], 0);
            return true;
        }
        if (evenZeros >= laneThreshold && oddZeros <= otherThreshold && TryDecodeCleanUtf16(bytes, StrictUtf16Be))
        {
            detected = new DetectedEncoding(StrictUtf16Be, [], 0);
            return true;
        }
        return false;
    }

    private static bool TryDecodeCleanUtf16(ReadOnlySpan<byte> bytes, Encoding encoding)
    {
        if (!TryDecode(bytes, encoding, out string text) || text.IndexOf('\0') >= 0 || text.IndexOf('\uFFFD') >= 0)
            return false;
        int printable = 0;
        foreach (char c in text)
            if (!char.IsControl(c) || c is '\r' or '\n' or '\t') printable++;
        return text.Length == 0 || printable * 10 >= text.Length * 9;
    }
}

public readonly record struct DetectedEncoding(Encoding Encoding, byte[] Preamble, int BodyOffset);
