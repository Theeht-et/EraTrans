using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;

namespace EraTrans.Core;

public sealed record EraAnalyzeDiagnostic(
    string Severity,
    int Level,
    string File,
    int Line,
    string Message);

public sealed record EraAnalyzeReport(
    string ProjectRoot,
    bool Success,
    int FileCount,
    long ElapsedMilliseconds,
    IReadOnlyList<EraAnalyzeDiagnostic> Diagnostics);

/// <summary>
/// Runs the built-in headless Emuera static analyzer (<c>tools/analyzer/Emuera.Core.dll</c>
/// from passed1999/era_mcp) directly in-process using the shared .NET 10 runtime inside a
/// collectible <see cref="AssemblyLoadContext"/> so static engine state is cleanly isolated
/// and released after every run.
/// </summary>
public sealed class EraAnalyzerService
{
    public const string AnalyzerTypeName = "MinorShift.Emuera.Analysis.EmueraAnalyzer";
    public const string AnalyzeMethodName = "AnalyzeProject";

    private static readonly object ProcessGate = new();

    public static string? ResolveBuiltInCoreDllPath(string? baseDirectory = null)
    {
        string root = string.IsNullOrWhiteSpace(baseDirectory)
            ? AppContext.BaseDirectory
            : baseDirectory;

        string[] candidates =
        [
            Path.Combine(root, "tools", "analyzer", "Emuera.Core.dll"),
            Path.Combine(root, "Emuera.Core.dll")
        ];

        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    public Task<EraAnalyzeReport> AnalyzeAsync(
        string gameCopyDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameCopyDirectory);

        if (!Directory.Exists(gameCopyDirectory))
        {
            throw new DirectoryNotFoundException($"游戏副本目录不存在：{gameCopyDirectory}");
        }

        string? coreDllPath = ResolveBuiltInCoreDllPath();
        if (string.IsNullOrWhiteSpace(coreDllPath))
        {
            throw new FileNotFoundException(
                "未找到内置无头分析引擎（tools/analyzer/Emuera.Core.dll）。请确认已完整解压 EraTrans 发布包，并保留 EXE 同级的 tools 文件夹。");
        }

        string fullGameCopy = Path.GetFullPath(gameCopyDirectory);
        return Task.Run(() => AnalyzeInProcess(coreDllPath, fullGameCopy, cancellationToken), cancellationToken);
    }

    private static EraAnalyzeReport AnalyzeInProcess(
        string coreDllPath,
        string gameCopyDirectory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (ProcessGate)
        {
            cancellationToken.ThrowIfCancellationRequested();

            CultureInfo previousCulture = CultureInfo.CurrentCulture;
            CultureInfo? previousDefaultCulture = CultureInfo.DefaultThreadCurrentCulture;
            var loadContext = new AnalyzerLoadContext(coreDllPath);

            try
            {
                Assembly coreAssembly = loadContext.LoadFromAssemblyPath(coreDllPath);
                Type analyzerType = coreAssembly.GetType(AnalyzerTypeName, throwOnError: true)!;
                MethodInfo analyzeMethod = analyzerType.GetMethod(
                    AnalyzeMethodName,
                    BindingFlags.Public | BindingFlags.Static,
                    binder: null,
                    types: [typeof(string), typeof(string)],
                    modifiers: null)
                    ?? throw new MissingMethodException(AnalyzerTypeName, AnalyzeMethodName);

                object? rawResult = analyzeMethod.Invoke(null, [gameCopyDirectory, null]);
                if (rawResult is null)
                {
                    throw new InvalidOperationException("内置分析引擎未返回诊断结果。");
                }

                return MapReflectionResult(gameCopyDirectory, rawResult);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                throw new InvalidOperationException($"引擎静态分析异常：{ex.InnerException.Message}", ex.InnerException);
            }
            finally
            {
                // EmueraAnalyzer sets CurrentCulture & DefaultThreadCurrentCulture to InvariantCulture;
                // restore the host WPF application's culture immediately.
                CultureInfo.CurrentCulture = previousCulture;
                CultureInfo.DefaultThreadCurrentCulture = previousDefaultCulture;
                loadContext.Unload();
            }
        }
    }

    private static EraAnalyzeReport MapReflectionResult(string gameCopyDirectory, object rawResult)
    {
        Type resultType = rawResult.GetType();
        bool success = resultType.GetProperty("Success")?.GetValue(rawResult) is bool b && b;
        int fileCount = resultType.GetProperty("FileCount")?.GetValue(rawResult) is int f ? f : 0;
        long elapsedMs = resultType.GetProperty("ElapsedMs")?.GetValue(rawResult) is long ms ? ms : 0;

        var diagnostics = new List<EraAnalyzeDiagnostic>();
        if (resultType.GetProperty("Diagnostics")?.GetValue(rawResult) is IEnumerable rawDiagnostics)
        {
            foreach (object? item in rawDiagnostics)
            {
                if (item is null)
                {
                    continue;
                }

                Type itemType = item.GetType();
                int level = itemType.GetProperty("Level")?.GetValue(item) is int lv ? lv : 0;
                string severity = itemType.GetProperty("Severity")?.GetValue(item) as string
                    ?? MapSeverity(level);
                string rawFile = itemType.GetProperty("File")?.GetValue(item) as string ?? string.Empty;
                int line = itemType.GetProperty("Line")?.GetValue(item) is int ln ? ln : 0;
                string message = itemType.GetProperty("Message")?.GetValue(item) as string ?? string.Empty;

                diagnostics.Add(new EraAnalyzeDiagnostic(
                    severity,
                    level,
                    FormatDiagnosticPath(gameCopyDirectory, rawFile),
                    line,
                    message));
            }
        }

        EraAnalyzeDiagnostic[] ordered = diagnostics
            .OrderByDescending(d => d.Level)
            .ThenBy(d => d.File, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.Line)
            .ToArray();

        return new EraAnalyzeReport(gameCopyDirectory, success, fileCount, elapsedMs, ordered);
    }

    private static string MapSeverity(int level) => level switch
    {
        >= 3 => "error",
        2 => "warning",
        1 => "info",
        _ => "hint"
    };

    private static string FormatDiagnosticPath(string projectRoot, string rawFile)
    {
        if (string.IsNullOrWhiteSpace(rawFile))
        {
            return string.Empty;
        }

        try
        {
            if (Path.IsPathRooted(rawFile))
            {
                string relative = Path.GetRelativePath(projectRoot, rawFile);
                if (!relative.StartsWith("..", StringComparison.Ordinal))
                {
                    return relative;
                }
            }
        }
        catch
        {
            // Keep original path text if relative computation fails.
        }

        return rawFile;
    }

    private sealed class AnalyzerLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;
        private readonly string _analyzerDirectory;

        public AnalyzerLoadContext(string coreDllPath)
            : base("EraTrans.BuiltInAnalyzer", isCollectible: true)
        {
            _resolver = new AssemblyDependencyResolver(coreDllPath);
            _analyzerDirectory = Path.GetDirectoryName(coreDllPath) ?? string.Empty;
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            string? assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
            if (!string.IsNullOrWhiteSpace(assemblyPath) && File.Exists(assemblyPath))
            {
                return LoadFromAssemblyPath(assemblyPath);
            }

            if (!string.IsNullOrWhiteSpace(assemblyName.Name))
            {
                string localCandidate = Path.Combine(_analyzerDirectory, assemblyName.Name + ".dll");
                if (File.Exists(localCandidate))
                {
                    return LoadFromAssemblyPath(localCandidate);
                }
            }

            // Defer framework assemblies to the host's shared .NET 10 runtime.
            return null;
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            string? libraryPath = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return !string.IsNullOrWhiteSpace(libraryPath)
                ? LoadUnmanagedDllFromPath(libraryPath)
                : IntPtr.Zero;
        }
    }
}
