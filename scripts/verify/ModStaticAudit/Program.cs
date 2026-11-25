using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace ModStaticAudit;

internal sealed record ModTargetSpec(
    string Name,
    string Category,
    string DirectoryPath);

internal sealed record CallSiteHit(
    string Dll,
    string CallerMethod,
    string TargetMethod,
    string Metric);

internal sealed class ModAuditSummary
{
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public string DirectoryPath { get; set; } = "";
    public List<string> ScannedDlls { get; set; } = [];
    public List<string> SkippedThirdPartyDlls { get; set; } = [];
    public int UnityRandomCalls { get; set; }
    public int SystemRandomCtorParameterless { get; set; }
    public int SystemRandomCtorSeeded { get; set; }
    public int DateTimeNowOrUtcNowCalls { get; set; }
    public int EnvironmentTickCountCalls { get; set; }
    public int StopwatchCalls { get; set; }
    public int UnityUnscaledOrRealtimeCalls { get; set; }
    public int GuidNewGuidCalls { get; set; }
    public int TaskRunCalls { get; set; }
    public int ThreadCtorCalls { get; set; }
    public int ThreadPoolQueueCalls { get; set; }
    public int DontDestroyOnLoadCalls { get; set; }
    public int SceneManagerLoadSceneCalls { get; set; }

    public int WallClockTotal =>
        DateTimeNowOrUtcNowCalls +
        EnvironmentTickCountCalls +
        StopwatchCalls +
        UnityUnscaledOrRealtimeCalls +
        GuidNewGuidCalls;

    public int ThreadingTotal =>
        TaskRunCalls +
        ThreadCtorCalls +
        ThreadPoolQueueCalls;

    public string RiskLevel { get; set; } = "LOW";
    public List<string> RiskReasons { get; set; } = [];
    public Dictionary<string, int> TargetBreakdown { get; set; } = new(StringComparer.Ordinal);
    public List<CallSiteHit> SampleCallSites { get; set; } = [];
    public List<string> Errors { get; set; } = [];
}

internal static class Program
{
    private static readonly HashSet<string> KnownNonModRuntimeDlls = new(StringComparer.OrdinalIgnoreCase)
    {
        "Newtonsoft.Json.dll",
        "MonoMod.dll",
        "MonoMod.RuntimeDetour.dll",
        "MonoMod.Utils.dll",
        "Mono.Cecil.dll",
        "Mono.Cecil.Mdb.dll",
        "Mono.Cecil.Pdb.dll",
        "Mono.Cecil.Rocks.dll",
        "mscorlib.dll",
        "netstandard.dll"
    };

    public static int Main(string[] args)
    {
        string repoRoot = FindRepoRoot();
        string cacheDir = Path.Combine(repoRoot, "artifacts", "mod-compat", "cache");
        string disabledDir = @"D:\SteamLibrary\steamapps\common\Hollow Knight\hollow_knight_Data\Managed\Mods\disabled";
        string outDir = Path.Combine(repoRoot, "artifacts", "mod-compat");

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--cache-dir" when i + 1 < args.Length:
                    cacheDir = Path.GetFullPath(args[++i]);
                    break;
                case "--disabled-dir" when i + 1 < args.Length:
                    disabledDir = Path.GetFullPath(args[++i]);
                    break;
                case "--out-dir" when i + 1 < args.Length:
                    outDir = Path.GetFullPath(args[++i]);
                    break;
            }
        }

        Directory.CreateDirectory(outDir);

        var targets = new List<ModTargetSpec>
        {
            // Auxiliary Mods (6)
            new("GodSeekerPlus", "Auxiliary", Path.Combine(disabledDir, "GodSeekerPlus")),
            new("DebugMod", "Auxiliary", Path.Combine(disabledDir, "DebugMod")),
            new("Benchwarp", "Auxiliary", Path.Combine(cacheDir, "Benchwarp")),
            new("Custom Knight", "Auxiliary", Path.Combine(cacheDir, "Custom Knight")),
            new("Enemy HP Bar", "Auxiliary", Path.Combine(cacheDir, "Enemy HP Bar")),
            new("SpeedRunQoL", "Auxiliary", Path.Combine(cacheDir, "SpeedRunQoL")),

            // Boss Mods (5)
            new("Inferno King Grimm", "BossMod", Path.Combine(cacheDir, "Inferno King Grimm")),
            new("Pale Prince", "BossMod", Path.Combine(cacheDir, "Pale Prince")),
            new("Pale Court", "BossMod", Path.Combine(cacheDir, "Pale Court")),
            new("TribeOfBattle", "BossMod", Path.Combine(cacheDir, "TribeOfBattle")),
            new("Shade Lord", "BossMod", Path.Combine(cacheDir, "Shade Lord")),

            // Core Dependencies (6)
            new("WeaverCore", "Dependency", Path.Combine(cacheDir, "WeaverCore")),
            new("FrogCore", "Dependency", Path.Combine(cacheDir, "FrogCore")),
            new("Satchel", "Dependency", Path.Combine(disabledDir, "Satchel")),
            new("SFCore", "Dependency", Path.Combine(disabledDir, "SFCore")),
            new("Osmi", "Dependency", Path.Combine(disabledDir, "Osmi")),
            new("Vasi", "Dependency", Path.Combine(disabledDir, "Vasi")),
        };

        var results = new List<ModAuditSummary>();
        foreach (var target in targets)
        {
            results.Add(AuditModDirectory(target));
        }

        string jsonPath = Path.Combine(outDir, "mod-dll-static-audit.json");
        string mdPath = Path.Combine(outDir, "mod-dll-static-audit.md");

        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        };
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(new
        {
            generatedAtUtc = DateTime.UtcNow.ToString("O"),
            cacheDir,
            disabledDir,
            modCount = results.Count,
            mods = results
        }, jsonOptions), new UTF8Encoding(false));

        File.WriteAllText(mdPath, BuildMarkdownReport(results), new UTF8Encoding(false));

        Console.WriteLine($"Audited {results.Count} mods/dependencies.");
        Console.WriteLine($"JSON: {jsonPath}");
        Console.WriteLine($"MD  : {mdPath}");
        return 0;
    }

    private static ModAuditSummary AuditModDirectory(ModTargetSpec spec)
    {
        var summary = new ModAuditSummary
        {
            Name = spec.Name,
            Category = spec.Category,
            DirectoryPath = spec.DirectoryPath
        };

        if (!Directory.Exists(spec.DirectoryPath))
        {
            summary.Errors.Add($"Directory not found: {spec.DirectoryPath}");
            summary.RiskLevel = "ERROR";
            summary.RiskReasons.Add("Directory missing");
            return summary;
        }

        var allDlls = Directory.GetFiles(spec.DirectoryPath, "*.dll", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (allDlls.Count == 0)
        {
            summary.Errors.Add("No .dll files found.");
            summary.RiskLevel = "ERROR";
            summary.RiskReasons.Add("No DLLs");
            return summary;
        }

        foreach (var dllPath in allDlls)
        {
            string fileName = Path.GetFileName(dllPath);
            string relPath = Path.GetRelativePath(spec.DirectoryPath, dllPath);

            // Skip bundled runtime/dependency libraries unless scanning that library itself
            if (KnownNonModRuntimeDlls.Contains(fileName) ||
                fileName.StartsWith("System.", StringComparison.OrdinalIgnoreCase) ||
                fileName.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase) ||
                (string.Equals(fileName, "WavLib.dll", StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(spec.Name, "WavLib", StringComparison.OrdinalIgnoreCase)))
            {
                summary.SkippedThirdPartyDlls.Add(relPath);
                continue;
            }

            summary.ScannedDlls.Add(relPath);
            AuditAssembly(dllPath, relPath, summary);
        }

        EvaluateRisk(summary);
        return summary;
    }

    private static void AuditAssembly(string dllPath, string relDllName, ModAuditSummary summary)
    {
        try
        {
            using var module = ModuleDefinition.ReadModule(dllPath, new ReaderParameters { ReadingMode = ReadingMode.Deferred });
            foreach (var type in GetAllTypes(module.Types))
            {
                foreach (var method in type.Methods)
                {
                    if (!method.HasBody) continue;
                    string callerName = $"{type.FullName}.{method.Name}";

                    foreach (var instr in method.Body.Instructions)
                    {
                        if (instr.Operand is not MethodReference mr) continue;
                        string? metric = ClassifyCall(mr);
                        if (metric is null) continue;

                        IncrementMetric(summary, metric);
                        string targetKey = $"{mr.DeclaringType.FullName}::{mr.Name}({string.Join(",", mr.Parameters.Select(p => p.ParameterType.Name))})";
                        summary.TargetBreakdown[targetKey] = summary.TargetBreakdown.GetValueOrDefault(targetKey) + 1;

                        if (summary.SampleCallSites.Count < 40)
                        {
                            summary.SampleCallSites.Add(new CallSiteHit(relDllName, callerName, targetKey, metric));
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            summary.Errors.Add($"Failed reading {relDllName}: {ex.Message}");
        }
    }

    private static IEnumerable<TypeDefinition> GetAllTypes(IEnumerable<TypeDefinition> types)
    {
        foreach (var t in types)
        {
            yield return t;
            if (t.HasNestedTypes)
            {
                foreach (var nt in GetAllTypes(t.NestedTypes))
                {
                    yield return nt;
                }
            }
        }
    }

    private static string? ClassifyCall(MethodReference mr)
    {
        string decl = mr.DeclaringType?.FullName ?? "";
        string name = mr.Name ?? "";

        if (decl == "UnityEngine.Random")
        {
            return "UnityRandom";
        }

        if (decl == "System.Random" && name == ".ctor")
        {
            return mr.Parameters.Count == 0 ? "SystemRandomCtorParameterless" : "SystemRandomCtorSeeded";
        }

        if (decl == "System.DateTime" && (name == "get_Now" || name == "get_UtcNow"))
        {
            return "DateTimeNowOrUtcNow";
        }

        if (decl == "System.Environment" && (name == "get_TickCount" || name == "get_TickCount64"))
        {
            return "EnvironmentTickCount";
        }

        if (decl == "System.Diagnostics.Stopwatch")
        {
            return "Stopwatch";
        }

        if (decl == "UnityEngine.Time" &&
            (name == "get_realtimeSinceStartup" ||
             name == "get_realtimeSinceStartupAsDouble" ||
             name == "get_unscaledTime" ||
             name == "get_unscaledTimeAsDouble"))
        {
            return "UnityUnscaledOrRealtime";
        }

        if (decl == "System.Guid" && name == "NewGuid")
        {
            return "GuidNewGuid";
        }

        if (decl == "System.Threading.Tasks.Task" && name == "Run")
        {
            return "TaskRun";
        }

        if (decl == "System.Threading.Thread" && name == ".ctor")
        {
            return "ThreadCtor";
        }

        if (decl == "System.Threading.ThreadPool" && name == "QueueUserWorkItem")
        {
            return "ThreadPoolQueue";
        }

        if (decl == "UnityEngine.Object" && name == "DontDestroyOnLoad")
        {
            return "DontDestroyOnLoad";
        }

        if (decl == "UnityEngine.SceneManagement.SceneManager" && name.StartsWith("LoadScene", StringComparison.Ordinal))
        {
            return "SceneManagerLoadScene";
        }

        return null;
    }

    private static void IncrementMetric(ModAuditSummary summary, string metric)
    {
        switch (metric)
        {
            case "UnityRandom": summary.UnityRandomCalls++; break;
            case "SystemRandomCtorParameterless": summary.SystemRandomCtorParameterless++; break;
            case "SystemRandomCtorSeeded": summary.SystemRandomCtorSeeded++; break;
            case "DateTimeNowOrUtcNow": summary.DateTimeNowOrUtcNowCalls++; break;
            case "EnvironmentTickCount": summary.EnvironmentTickCountCalls++; break;
            case "Stopwatch": summary.StopwatchCalls++; break;
            case "UnityUnscaledOrRealtime": summary.UnityUnscaledOrRealtimeCalls++; break;
            case "GuidNewGuid": summary.GuidNewGuidCalls++; break;
            case "TaskRun": summary.TaskRunCalls++; break;
            case "ThreadCtor": summary.ThreadCtorCalls++; break;
            case "ThreadPoolQueue": summary.ThreadPoolQueueCalls++; break;
            case "DontDestroyOnLoad": summary.DontDestroyOnLoadCalls++; break;
            case "SceneManagerLoadScene": summary.SceneManagerLoadSceneCalls++; break;
        }
    }

    private static void EvaluateRisk(ModAuditSummary s)
    {
        if (s.Errors.Count > 0)
        {
            s.RiskLevel = "ERROR";
            s.RiskReasons.Add("Scan errors");
            return;
        }

        if (s.SystemRandomCtorParameterless > 0)
            s.RiskReasons.Add($"System.Random() x{s.SystemRandomCtorParameterless}");
        if (s.WallClockTotal > 0)
            s.RiskReasons.Add($"WallClock/SysSource x{s.WallClockTotal}");
        if (s.ThreadingTotal > 0)
            s.RiskReasons.Add($"Threading x{s.ThreadingTotal}");
        if (s.UnityRandomCalls > 0)
            s.RiskReasons.Add($"UnityEngine.Random x{s.UnityRandomCalls}");
        if (s.DontDestroyOnLoadCalls > 0)
            s.RiskReasons.Add($"DontDestroyOnLoad x{s.DontDestroyOnLoadCalls}");
        if (s.SceneManagerLoadSceneCalls > 0)
            s.RiskReasons.Add($"LoadScene* x{s.SceneManagerLoadSceneCalls}");

        if (s.SystemRandomCtorParameterless > 0 || s.ThreadingTotal > 0 || s.WallClockTotal >= 5)
        {
            s.RiskLevel = "HIGH";
        }
        else if (s.WallClockTotal > 0 || s.DontDestroyOnLoadCalls > 0 || s.SceneManagerLoadSceneCalls > 0 || s.UnityRandomCalls > 0)
        {
            s.RiskLevel = "MEDIUM";
        }
        else
        {
            s.RiskLevel = "LOW";
            s.RiskReasons.Add("No static determinism hazards detected");
        }
    }

    private static string BuildMarkdownReport(List<ModAuditSummary> results)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# 第三方 Mod 与核心依赖库 DLL 静态体检报告");
        sb.AppendLine();
        sb.AppendLine($"- 生成时间 (UTC): `{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z`");
        sb.AppendLine($"- 扫描对象总数: `{results.Count}` (6 辅助 Mod + 5 Boss Mod + 6 核心依赖库)");
        sb.AppendLine();
        sb.AppendLine("## 风险汇总表");
        sb.AppendLine();
        sb.AppendLine("| Mod / 依赖库 | 分类 | 扫描 DLL | `UnityEngine.Random` | `System.Random()` (无参/带参) | 墙钟/系统源 (`Now`/`Tick`/`Stopwatch`/`realtime`/`unscaled`/`Guid`) | 多线程 (`Task.Run`/`Thread`/`ThreadPool`) | `DontDestroyOnLoad` | `LoadScene*` | 风险等级 |");
        sb.AppendLine("| --- | --- | --- | ---: | :---: | ---: | ---: | ---: | ---: | :---: |");

        foreach (var r in results)
        {
            string dlls = r.ScannedDlls.Count > 0 ? string.Join(", ", r.ScannedDlls) : "(none)";
            string sysRand = $"{r.SystemRandomCtorParameterless} / {r.SystemRandomCtorSeeded}";
            string wallDetail = $"{r.WallClockTotal} ({r.DateTimeNowOrUtcNowCalls}/{r.EnvironmentTickCountCalls}/{r.StopwatchCalls}/{r.UnityUnscaledOrRealtimeCalls}/{r.GuidNewGuidCalls})";
            string threadDetail = $"{r.ThreadingTotal} ({r.TaskRunCalls}/{r.ThreadCtorCalls}/{r.ThreadPoolQueueCalls})";
            sb.AppendLine($"| `{r.Name}` | {r.Category} | `{dlls}` | {r.UnityRandomCalls} | {sysRand} | {wallDetail} | {threadDetail} | {r.DontDestroyOnLoadCalls} | {r.SceneManagerLoadSceneCalls} | **{r.RiskLevel}** |");
        }

        sb.AppendLine();
        sb.AppendLine("> 墙钟/系统源括号细分格式：`总数 (DateTime.Now/UtcNow / Environment.TickCount / Stopwatch / Time.realtimeSinceStartup|unscaledTime / Guid.NewGuid)`");
        sb.AppendLine("> 多线程括号细分格式：`总数 (Task.Run / Thread..ctor / ThreadPool.QueueUserWorkItem)`");
        sb.AppendLine();
        sb.AppendLine("## 逐项明细与调用点样本");
        sb.AppendLine();

        foreach (var r in results)
        {
            sb.AppendLine($"### `{r.Name}` ({r.Category}) — **{r.RiskLevel}**");
            sb.AppendLine($"- 目录: `{r.DirectoryPath}`");
            sb.AppendLine($"- 已扫描 DLL: `{string.Join(", ", r.ScannedDlls)}`");
            if (r.SkippedThirdPartyDlls.Count > 0)
            {
                sb.AppendLine($"- 已跳过通用运行时/附带 DLL: `{string.Join(", ", r.SkippedThirdPartyDlls)}`");
            }
            if (r.Errors.Count > 0)
            {
                sb.AppendLine($"- 错误: `{string.Join("; ", r.Errors)}`");
            }
            sb.AppendLine($"- 风险摘要: {string.Join("; ", r.RiskReasons)}");

            if (r.TargetBreakdown.Count > 0)
            {
                sb.AppendLine("- 目标 API 调用频次:");
                foreach (var kv in r.TargetBreakdown.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal))
                {
                    sb.AppendLine($"  - `{kv.Key}`: {kv.Value}");
                }
            }

            if (r.SampleCallSites.Count > 0)
            {
                sb.AppendLine("- 命中调用点样本 (最多 40 条):");
                foreach (var hit in r.SampleCallSites)
                {
                    sb.AppendLine($"  - `[{hit.Metric}]` `{hit.Dll}` | `{hit.CallerMethod}` -> `{hit.TargetMethod}`");
                }
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "HollowKnightTAS.sln")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return Directory.GetCurrentDirectory();
    }
}
