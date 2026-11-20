using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

internal static partial class StudioScenarioHarness
{
    static async Task RunPackageSmokeAsync()
    {
        async Task<string> Run(string name, string executable, string[] arguments, string input = "")
        {
            var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Tools", executable))
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = output,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            start.Environment["PATH"] = "";
            start.Environment["DOTNET_ROOT"] = Path.Combine(output, "missing-dotnet");
            start.Environment["DOTNET_ROOT_X64"] = Path.Combine(output, "missing-dotnet");
            start.Environment["DOTNET_MULTILEVEL_LOOKUP"] = "0";
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteAsync(input);
            process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            var result = await stdout;
            await File.WriteAllTextAsync(Path.Combine(output, name + ".stdout.jsonl"), result);
            await File.WriteAllTextAsync(Path.Combine(output, name + ".stderr.txt"), await stderr);
            Require(process.ExitCode == 0, name + " packaged executable exit zero without system runtime");
            return result;
        }
        using var cli = JsonDocument.Parse(await Run("package-cli", "HollowKnightTAS.Cli.exe",
            new[] { "automation", "call", "fullRunStatus", "observe.status" }));
        Require(cli.RootElement.GetProperty("success").GetString() == "true", "packaged CLI reads live paused game");
        var input = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-11-25\",\"capabilities\":{},\"clientInfo\":{\"name\":\"package-smoke\",\"version\":\"1\"}}}\n"
            + "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}\n"
            + "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\",\"params\":{}}\n"
            + "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"ping\",\"params\":{}}\n";
        var lines = (await Run("package-mcp", "HollowKnightTAS.AgentBridge.exe", Array.Empty<string>(), input))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Require(lines.Length == 3, "MCP returns initialize, tools/list and ping responses");
        foreach (var line in lines)
        {
            using var response = JsonDocument.Parse(line);
            Require(response.RootElement.TryGetProperty("result", out _), "MCP response has result");
        }
        using var tools = JsonDocument.Parse(lines[1]);
        Require(tools.RootElement.GetProperty("result").GetProperty("tools").GetArrayLength() > 0,
            "MCP returns packaged tool catalog");
    }
}
