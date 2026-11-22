namespace EraTrans.Core.VisibleText;

/// <summary>Where one source line sits: its function, the screen it is shown on, and its branch.</summary>
internal sealed record ScreenLayout(string FunctionId, string ScreenId, string BranchPath, string BranchLabel);

/// <summary>
/// Groups ERB lines into "screens": the block of text the player sees before the game waits
/// for input. A screen starts at a function label or right after an interaction command, and
/// it does not split at <c>IF</c>/<c>SELECTCASE</c> branches, because the alternatives belong
/// to the same visible surface and should share terminology.
/// </summary>
internal static class VisibleTextScreenMap
{
    /// <summary>Commands that hand control to the player, so whatever follows is a new screen.</summary>
    private static readonly HashSet<string> ScreenEndingCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "WAIT", "WAITKEY", "TWAIT", "INPUT", "INPUTS", "BINPUT", "CLEARLINE", "QUIT", "FORCE_QUIT"
    };

    public static Dictionary<int, ScreenLayout> Build(string text)
    {
        var map = new Dictionary<int, ScreenLayout>();
        var frames = new List<Frame>();
        int ifCount = 0;
        int selectCount = 0;
        int loopCount = 0;
        int screenIndex = 0;
        string functionId = "(global)";

        int lineNumber = 0;
        int start = 0;
        while (start <= text.Length)
        {
            int newline = text.IndexOf('\n', start);
            string line = newline < 0 ? text[start..] : text[start..newline];
            lineNumber++;
            string trimmed = line.TrimStart();

            if (trimmed.StartsWith('@') && trimmed.Length > 1)
            {
                string name = ReadFunctionName(trimmed);
                if (name.Length > 0)
                {
                    functionId = name;
                    frames.Clear();
                    screenIndex = 0;
                }
            }
            else
            {
                string command = ReadCommand(trimmed);
                switch (command.ToUpperInvariant())
                {
                    case "IF":
                        frames.Add(new Frame("if" + (++ifCount).ToString("D3"), "if", string.Empty));
                        break;
                    case "SIF":
                        break;
                    case "ELSEIF":
                    case "ELSE":
                        SetBranch(frames, "if", command.Equals("ELSE", StringComparison.OrdinalIgnoreCase) ? "ELSE" : "ELSEIF");
                        break;
                    case "ENDIF":
                        PopFrame(frames, "if");
                        break;
                    case "SELECTCASE":
                        frames.Add(new Frame("sel" + (++selectCount).ToString("D3"), "sel", string.Empty));
                        break;
                    case "CASE":
                    case "CASEELSE":
                        SetBranch(frames, "sel", command.Equals("CASEELSE", StringComparison.OrdinalIgnoreCase) ? "ELSE" : "CASE");
                        break;
                    case "ENDSELECT":
                        PopFrame(frames, "sel");
                        break;
                    case "FOR":
                    case "WHILE":
                    case "REPEAT":
                        frames.Add(new Frame("loop" + (++loopCount).ToString("D3"), "loop", string.Empty));
                        break;
                    case "NEXT":
                    case "WEND":
                    case "REND":
                        PopFrame(frames, "loop");
                        break;
                }

                if (ScreenEndingCommands.Contains(command))
                {
                    // The waiting line itself still belongs to the screen it closes.
                    screenIndex++;
                }
            }

            map[lineNumber] = new ScreenLayout(
                functionId,
                functionId + "#" + screenIndex.ToString("D4"),
                BuildBranchPath(frames),
                CurrentBranchLabel(frames));

            if (newline < 0)
            {
                break;
            }

            start = newline + 1;
        }

        return map;
    }

    public static ScreenLayout Resolve(Dictionary<int, ScreenLayout> map, int line) =>
        map.TryGetValue(line, out ScreenLayout? layout)
            ? layout
            : new ScreenLayout("(global)", "(global)#0000", string.Empty, string.Empty);

    private static string BuildBranchPath(List<Frame> frames) =>
        frames.Count == 0
            ? string.Empty
            : string.Join("/", frames.Select(frame => frame.Id + ":" + frame.Kind + ":" + frame.Branch));

    private static string CurrentBranchLabel(List<Frame> frames)
    {
        for (int index = frames.Count - 1; index >= 0; index--)
        {
            if (frames[index].Branch.Length > 0)
            {
                return frames[index].Branch;
            }
        }

        return string.Empty;
    }

    private static void SetBranch(List<Frame> frames, string kind, string branch)
    {
        for (int index = frames.Count - 1; index >= 0; index--)
        {
            if (frames[index].Kind == kind)
            {
                frames[index] = frames[index] with { Branch = branch };
                return;
            }
        }
    }

    private static void PopFrame(List<Frame> frames, string kind)
    {
        for (int index = frames.Count - 1; index >= 0; index--)
        {
            if (frames[index].Kind == kind)
            {
                frames.RemoveAt(index);
                return;
            }
        }
    }

    private static string ReadFunctionName(string trimmedLine)
    {
        int index = 1;
        while (index < trimmedLine.Length &&
               !char.IsWhiteSpace(trimmedLine[index]) &&
               trimmedLine[index] != '(' &&
               trimmedLine[index] != ',' &&
               trimmedLine[index] != ';')
        {
            index++;
        }

        return trimmedLine[1..index];
    }

    private static string ReadCommand(string trimmedLine)
    {
        int index = 0;
        while (index < trimmedLine.Length && !char.IsWhiteSpace(trimmedLine[index]))
        {
            index++;
        }

        return trimmedLine[..index];
    }

    private sealed record Frame(string Id, string Kind, string Branch);
}
