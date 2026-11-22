using System.Text;

namespace EraTrans.Core.WriteBack;

/// <summary>
/// Reads and writes one game source file using that file's own encoding and byte-order mark.
/// Real Emuera games mix UTF-8 (with or without BOM) and Shift-JIS inside the same directory,
/// so a single global setting cannot be used when translating back into a game copy.
/// </summary>
public sealed class GameFileText
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private GameFileText(string text, Encoding encoding, byte[] preamble)
    {
        Text = text;
        Encoding = encoding;
        Preamble = preamble;
    }

    public string Text { get; set; }

    public Encoding Encoding { get; }

    /// <summary>Bytes written before the encoded body (empty when the file had no BOM).</summary>
    public byte[] Preamble { get; }

    public bool HasBom => Preamble.Length > 0;

    public static GameFileText Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        byte[] bytes = File.ReadAllBytes(path);
        var detected = DetectEncoding(bytes);
        string text = detected.Encoding.GetString(bytes, detected.BodyOffset, bytes.Length - detected.BodyOffset);
        return new GameFileText(text, detected.Encoding, detected.Preamble);
    }

    /// <summary>Writes the text back with the same encoding, preamble and (untouched) line endings.</summary>
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

    /// <summary>
    /// Detects the encoding of a game file: BOM first, then strict UTF-8, then Shift-JIS (CP932).
    /// Strict UTF-8 keeps Shift-JIS files from being mis-decoded into replacement characters.
    /// </summary>
    public static DetectedEncoding DetectEncoding(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return new DetectedEncoding(Utf8NoBom, [0xEF, 0xBB, 0xBF], 3);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return new DetectedEncoding(Encoding.Unicode, [0xFF, 0xFE], 2);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return new DetectedEncoding(Encoding.BigEndianUnicode, [0xFE, 0xFF], 2);
        }

        try
        {
            StrictUtf8.GetString(bytes);
            return new DetectedEncoding(Utf8NoBom, [], 0);
        }
        catch (DecoderFallbackException)
        {
            return new DetectedEncoding(GetShiftJis(), [], 0);
        }
    }

    private static Encoding GetShiftJis()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(932);
    }
}

public readonly record struct DetectedEncoding(Encoding Encoding, byte[] Preamble, int BodyOffset);
