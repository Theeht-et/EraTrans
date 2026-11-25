using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace EraTrans.Core;

public sealed record EraAnalyzeDiagnostic(
    string Severity,
    string File,
    int Line,
    string Message);

public sealed record EraAnalyzeReport(
    string ProjectRoot,
    bool Success,
    int FileCount,
    long ElapsedMilliseconds,
    IReadOnlyList<EraAnalyzeDiagnostic> Diagnostics,
    string RawOutput);

/// <summary>
/// Optional integration with the headless Emuera static analyzer CLI (<c>era-analyze</c>
/// from passed1999/era_mcp) to validate generated game copies in seconds without launching
/// the GUI engine or touching save files.
/// </summary>
public sealed class EraAnalyzerService
{
    private static readonly Regex DiagnosticLineRegex = new(
        @"^\s*\[(?<sev>[^\]]+)\]\s+(?<file>.+?):(?<line>\d+)\s+(?<msg>.+)$",
        RegexOptions.Compiled);

    public static string? ResolveExecutablePath(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            string trimmed = configuredPath.Trim().Trim('"');
            if (File.Exists(trimmed))
            {
                return Path.GetFullPath(trimmed);
            }
        }

        string baseDir = AppContext.BaseDirectory;
        string[] localCandidates =
        [
            Path.Combine(baseDir, "era-analyze.exe"),
            Path.Combine(baseDir, "tools", "era-analyze.exe"),
            Path.Combine(baseDir, "era-analyze"),
            Path.Combine(baseDir, "tools", "era-analyze")
        ];

        foreach (string candidate in localCandidates)
        {
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    public async Task<EraAnalyzeReport> AnalyzeAsync(
        string executablePath,
        string gameCopyDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameCopyDirectory);

        if (!File.Exists(executablePath))
        {
            throw new FileNotFoundException($"找不到无头分析引擎：{executablePath}", executablePath);
        }

        if (!Directory.Exists(gameCopyDirectory))
        {
            throw new DirectoryNotFoundException($"游戏副本目录不存在：{gameCopyDirectory}");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add(Path.GetFullPath(gameCopyDirectory));

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);
        string stdout = await stdoutTask;
        string stderr = await stderrTask;

        string combined = string.IsNullOrWhiteSpace(stderr)
            ? stdout
            : stdout + Environment.NewLine + stderr;

        return ParseOutput(gameCopyDirectory, combined);
    }

    public static EraAnalyzeReport ParseOutput(string projectRoot, string rawOutput)
    {
        string text = rawOutput ?? string.Empty;
        bool success = false;
        int fileCount = 0;
        long elapsedMs = 0;
        var diagnostics = new List<EraAnalyzeDiagnostic>();

        foreach (string rawLine in text.Split(["\r\n", "\n", "\r"], StringSplitOptions.None))
        {
            string line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (TryReadHeaderValue(line, "files", out string filesVal) &&
                int.TryParse(filesVal, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedFiles))
            {
                fileCount = parsedFiles;
                continue;
            }

            if (TryReadHeaderValue(line, "success", out string successVal) &&
                bool.TryParse(successVal, out bool parsedSuccess))
            {
                success = parsedSuccess;
                continue;
            }

            if (TryReadHeaderValue(line, "elapsedMs", out string elapsedVal) &&
                long.TryParse(elapsedVal, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsedElapsed))
            {
                elapsedMs = parsedElapsed;
                continue;
            }

            Match match = DiagnosticLineRegex.Match(line);
            if (match.Success)
            {
                string sev = match.Groups["sev"].Value.Trim();
                string file = match.Groups["file"].Value.Trim();
                int.TryParse(match.Groups["line"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int lineNumber);
                string msg = match.Groups["msg"].Value.Trim();
                diagnostics.Add(new EraAnalyzeDiagnostic(sev, file, lineNumber, msg));
            }
        }

        return new EraAnalyzeReport(projectRoot, success, fileCount, elapsedMs, diagnostics, text);
    }

    private static bool TryReadHeaderValue(string line, string key, out string value)
    {
        value = string.Empty;
        if (!line.StartsWith(key, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        int colon = line.IndexOf(':');
        if (colon < key.Length)
        {
            return false;
        }

        if (!line[..colon].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        value = line[(colon + 1)..].Trim();
        return true;
    }
}
