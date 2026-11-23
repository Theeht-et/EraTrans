namespace EraTrans.Core.WriteBack;

/// <summary>
/// A half-open character range <c>[Start, End)</c> inside one decoded game source file.
/// Positions are character offsets (not bytes) so they can be used directly against the
/// string produced by <see cref="GameFileText"/> for that file's own encoding.
/// </summary>
public readonly record struct TextSpan(int Start, int End)
{
    public int Length => End - Start;

    public bool IsValid => Start >= 0 && End >= Start;

    public bool Overlaps(TextSpan other) => Start < other.End && other.Start < End;

    public override string ToString() => $"[{Start},{End})";
}
