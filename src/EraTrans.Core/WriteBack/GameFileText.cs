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

        // UTF-16 without a BOM: every ASCII character leaves a 0x00 byte in the high position,
        // which strict UTF-8 happily accepts, so this check has to run before the UTF-8 probe.
        if (TryDetectBomlessUtf16(bytes, out DetectedEncoding utf16))
        {
            return utf16;
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

    /// <summary>
    /// Detects BOM-less UTF-16 by looking at where the zero bytes fall: UTF-16LE ASCII text keeps
    /// 0x00 in every odd position, UTF-16BE in every even one. Real ERB scripts are mostly ASCII
    /// syntax with Japanese literals, so a quarter of the stream being zero bytes in one lane and
    /// almost none in the other is a reliable signal.
    /// </summary>
    private static bool TryDetectBomlessUtf16(ReadOnlySpan<byte> bytes, out DetectedEncoding detected)
    {
        detected = default;
        if (bytes.Length < 16 || bytes.Length % 2 != 0)
        {
            return false;
        }

        int evenZeros = 0;
        int oddZeros = 0;
        for (int i = 0; i < bytes.Length; i += 2)
        {
            if (bytes[i] == 0)
            {
                evenZeros++;
            }

            if (bytes[i + 1] == 0)
            {
                oddZeros++;
            }
        }

        int pairs = bytes.Length / 2;
        int laneThreshold = pairs / 4;
        int otherThreshold = Math.Max(1, pairs / 50);

        if (oddZeros >= laneThreshold && evenZeros <= otherThreshold)
        {
            detected = new DetectedEncoding(Encoding.Unicode, [], 0);
            return true;
        }

        if (evenZeros >= laneThreshold && oddZeros <= otherThreshold)
        {
            detected = new DetectedEncoding(Encoding.BigEndianUnicode, [], 0);
            return true;
        }

        return false;
    }

    private static Encoding GetShiftJis()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(932);
    }
}

public readonly record struct DetectedEncoding(Encoding Encoding, byte[] Preamble, int BodyOffset);
