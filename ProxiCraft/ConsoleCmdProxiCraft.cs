using System;
using System.Collections.Generic;

namespace ProxiCraft;

/// <summary>
/// Console command for ProxiCraft diagnostics and troubleshooting.
/// Usage: pc [subcommand]
/// 
/// Subcommands:
///   status   - Show mod status and configuration
///   health   - Show startup health check results
///   diag     - Full diagnostic report
///   test     - Test container scanning
///   reload   - Reload configuration
///   toggle   - Enable/disable mod
///   conflicts - Show detected conflicts
/// </summary>
public class ConsoleCmdProxiCraft : ConsoleCmdAbstract
{
    public override string[] getCommands()
    {
        return new[] { "proxicraft", "pc" };
    }

    public override string getDescription()
    {
        return "ProxiCraft mod diagnostics and control";
    }

    public override string getHelp()
    {
        return @"Usage: pc [command]

Commands:
  status     - Show current mod status and configuration
  health     - Show startup health check results
  recheck    - Re-run startup health check
  fullcheck  - Full diagnostic report (for bug reports)
  diag       - Show mod compatibility report
  test       - Test container scanning (shows nearby containers)
  perf       - Performance profiler (pc perf on/off/reset/report)
  reload     - Reload configuration from config.json
  toggle     - Toggle mod on/off
  conflicts  - Show detected mod conflicts
  debug      - Toggle debug logging

Performance Commands:
  pc perf          - Show brief performance status
  pc perf on       - Enable profiling (collects timing data)
  pc perf off      - Disable profiling
  pc perf reset    - Clear profiling data
  pc perf report   - Show detailed performance report

Examples:
  pc status
  pc health
  pc fullcheck
  pc test
  pc perf on
  pc perf report
";
    }

    public override void Execute(List<string> _params, CommandSenderInfo _senderInfo)
    {
        try
        {
            string subCommand = _params.Count > 0 ? _params[0].ToLower() : "status";

            switch (subCommand)
            {
                case "status":
                    ShowStatus();
                    break;

                case "health":
                    ShowHealthCheck();
                    break;

                case "recheck":
                    RunRecheck();
                    break;

                case "fullcheck":
                case "full":
                    RunFullCheck();
                    break;

                case "diag":
                case "diagnostic":
                case "diagnostics":
                    ShowDiagnostics();
                    break;
                    
                case "test":
                    TestContainerScan();
                    break;
                    
                case "reload":
                    ReloadConfig();
                    break;
                    
                case "toggle":
                    ToggleMod();
                    break;
                    
                case "conflicts":
                    ShowConflicts();
                    break;
                    
                case "debug":
                    ToggleDebug();
                    break;
                
                case "perf":
                case "performance":
                    HandlePerfCommand(_params);
                    break;
                    
                default:
                    Output($"Unknown command: {subCommand}. Use 'pc help' for available commands.");
                    break;
            }
        }
        catch (Exception ex)
        {
            OutputError($"Command error: {ex.Message}");
            Output("Use 'pc diag' for troubleshooting information.");
        }
    }

    private void Output(string message)
    {
        SingletonMonoBehaviour<SdtdConsole>.Instance.Output(message);
    }

    private void OutputWarning(string message)
    {
        SingletonMonoBehaviour<SdtdConsole>.Instance.Output($"[Warning] {message}");
    }

    private void OutputError(string message)
    {
        SingletonMonoBehaviour<SdtdConsole>.Instance.Output($"[Error] {message}");
    }

    private void ShowStatus()
    {
        var config = ProxiCraft.Config;
        
        Output("=== ProxiCraft Status ===");
        Output($"  Mod Version: {ProxiCraft.MOD_VERSION}");
        Output($"  Enabled: {(config?.modEnabled == true ? "YES" : "NO")}");
        Output($"  Debug Mode: {(config?.isDebug == true ? "ON" : "OFF")}");
        Output($"  Verbose Health Check: {(config?.verboseHealthCheck == true ? "ON" : "OFF")}");
        Output("");
        Output("  Features:");
        Output($"    Crafting: {GetFeatureStatus(config?.modEnabled)}");
        Output($"    Repair/Upgrade: {GetFeatureStatus(config?.enableForRepairAndUpgrade)}");
        Output($"    Trader: {GetFeatureStatus(config?.enableForTrader)}");
        Output($"    Reload: {GetFeatureStatus(config?.enableForReload)}");
        Output($"    Refuel: {GetFeatureStatus(config?.enableForRefuel)}");
        Output($"    Vehicle Storage: {GetFeatureStatus(config?.enableFromVehicles)}");
        Output("");
        Output($"  Range: {(config?.range <= 0 ? "Unlimited" : $"{config?.range} blocks")}");
        
        var failedPatches = ModCompatibility.GetFailedPatches();
        int failCount = 0;
        foreach (var _ in failedPatches) failCount++;
        
        if (failCount > 0)
        {
            OutputWarning($"{failCount} patch(es) failed - use 'pc diag' for details");
        }
        
        var conflicts = ModCompatibility.GetConflicts();
        if (conflicts.Count > 0)
        {
            OutputWarning($"{conflicts.Count} potential conflict(s) detected - use 'pc conflicts' for details");
        }
    }

    private string GetFeatureStatus(bool? enabled)
    {
        return enabled == true ? "Enabled" : "Disabled";
    }

    private void ShowDiagnostics()
    {
        string report = ModCompatibility.GetDiagnosticReport();
        
        // Split and log each line
        foreach (var line in report.Split('\n'))
        {
            Output(line);
        }
    }

    private void TestContainerScan()
    {
        Output("=== Container Scan Test ===");
        
        var player = GameManager.Instance?.World?.GetPrimaryPlayer();
        if (player == null)
        {
            Output("  Error: No player found. Must be in-game to test.");
            return;
        }

        Output($"  Player Position: {player.position}");
        Output($"  Scan Range: {(ProxiCraft.Config?.range <= 0 ? "Unlimited" : $"{ProxiCraft.Config?.range} blocks")}");
        Output("");

        try
        {
            var items = ContainerManager.GetStorageItems(ProxiCraft.Config);
            
            Output($"  Found {items.Count} item stacks in nearby containers");
            
            if (items.Count > 0)
            {
                // Group by item type and show summary
                var grouped = new Dictionary<string, int>();
                foreach (var item in items)
                {
                    if (item == null || item.IsEmpty()) continue;
                    string name = item.itemValue?.ItemClass?.GetItemName() ?? "Unknown";
                    if (grouped.ContainsKey(name))
                        grouped[name] += item.count;
                    else
                        grouped[name] = item.count;
                }

                Output("  Items found:");
                int shown = 0;
                foreach (var kvp in grouped)
                {
                    Output($"    - {kvp.Key}: {kvp.Value}");
                    if (++shown >= 20)
                    {
                        Output($"    ... and {grouped.Count - 20} more item types");
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            OutputError($"Scan failed: {ex.Message}");
            Output("  This may indicate a compatibility issue with another mod.");
        }
    }

    private void ReloadConfig()
    {
        try
        {
            // Re-run config loading (would need to expose this method)
            Output("Configuration reload requested.");
            Output("Note: Full reload requires game restart. Some settings may not update.");
            
            // For now just show current config
            ShowStatus();
        }
        catch (Exception ex)
        {
            OutputError($"Config reload failed: {ex.Message}");
        }
    }

    private void ToggleMod()
    {
        if (ProxiCraft.Config == null)
        {
            OutputError("Configuration not loaded!");
            return;
        }

        ProxiCraft.Config.modEnabled = !ProxiCraft.Config.modEnabled;
        Output($"ProxiCraft is now {(ProxiCraft.Config.modEnabled ? "ENABLED" : "DISABLED")}");
        Output("Note: This change is temporary. Edit config.json for permanent changes.");
    }

    private void ShowConflicts()
    {
        var conflicts = ModCompatibility.GetConflicts();
        
        Output("=== Detected Mod Conflicts ===");
        
        if (conflicts.Count == 0)
        {
            Output("  No conflicts detected.");
            return;
        }

        foreach (var conflict in conflicts)
        {
            OutputWarning($"[{conflict.ConflictType}]");
            OutputWarning($"  Mod: {conflict.ModName}");
            OutputWarning($"  Issue: {conflict.Description}");
            Output($"  Recommendation: {conflict.Recommendation}");
            Output("");
        }
    }

    private void ToggleDebug()
    {
        if (ProxiCraft.Config == null)
        {
            OutputError("Configuration not loaded!");
            return;
        }

        ProxiCraft.Config.isDebug = !ProxiCraft.Config.isDebug;
        Output($"Debug logging is now {(ProxiCraft.Config.isDebug ? "ON" : "OFF")}");
    }

    private void ShowHealthCheck()
    {
        if (!StartupHealthCheck.HasRun)
        {
            Output("Health check has not been run yet.");
            Output("Use 'pc recheck' to run it now.");
            return;
        }

        Output(StartupHealthCheck.GetHealthReport());
    }

    private void RunRecheck()
    {
        Output("Re-running startup health check...");
        Output("");

        StartupHealthCheck.Recheck();

        Output("");
        Output("Health check complete. Use 'pc health' to view results.");
    }

    private void RunFullCheck()
    {
        Output("Running full diagnostic check...");
        Output("");

        string report = StartupHealthCheck.GetFullDiagnosticReport();

        // Split and output each line
        foreach (var line in report.Split('\n'))
        {
            Output(line);
        }
    }

    private void HandlePerfCommand(List<string> args)
    {
        string subCmd = args.Count >= 2 ? args[1].ToLowerInvariant() : "";
        
        switch (subCmd)
        {
            case "on":
            case "enable":
                PerformanceProfiler.IsEnabled = true;
                PerformanceProfiler.Reset(); // Start fresh when enabling
                Output("Performance profiler ENABLED. Use 'pc perf' or 'pc perf report' to view results.");
                break;
                
            case "off":
            case "disable":
                PerformanceProfiler.IsEnabled = false;
                Output("Performance profiler DISABLED.");
                break;
                
            case "reset":
            case "clear":
                PerformanceProfiler.Reset();
                Output("Performance profiler data cleared.");
                break;
                
            case "report":
            case "full":
                ShowPerfReport(detailed: true, saveToFile: true);
                break;
            
            case "save":
            case "export":
            case "file":
                SavePerfReportToFile();
                break;
                
            default:
                // Show brief status
                ShowPerfReport(detailed: false, saveToFile: false);
                break;
        }
    }

    private void SavePerfReportToFile()
    {
        try
        {
            string report = $"=== ProxiCraft Performance Report ===\n";
            report += $"Generated: {System.DateTime.Now:yyyy-MM-dd HH:mm:ss}\n";
            report += $"Profiler: {(PerformanceProfiler.IsEnabled ? "ENABLED" : "DISABLED")}\n\n";
            report += PerformanceProfiler.GetReport();
            
            // Write to mod folder
            string modPath = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            string filePath = System.IO.Path.Combine(modPath, "perf_report.txt");
            
            System.IO.File.WriteAllText(filePath, report);
            
            return; // Called from ShowPerfReport, don't output again
        }
        catch (System.Exception ex)
        {
            ProxiCraft.LogWarning($"Failed to save perf report: {ex.Message}");
        }
    }

    private void ShowPerfReport(bool detailed, bool saveToFile)
    {
        Output("=== ProxiCraft Performance ===");
        Output($"  Profiler: {(PerformanceProfiler.IsEnabled ? "ENABLED" : "DISABLED")}");
        Output("");
        
        if (!PerformanceProfiler.IsEnabled && !PerformanceProfiler.HasData)
        {
            Output("  No data collected. Use 'pc perf on' to enable profiling.");
            return;
        }
        
        if (detailed)
        {
            string report = PerformanceProfiler.GetReport();
            foreach (var line in report.Split('\n'))
            {
                Output(line);
            }
            
            // Auto-save to file for easy sharing
            if (saveToFile)
            {
                SavePerfReportToFile();
                string modPath = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                Output("");
                Output($"Report saved to: {System.IO.Path.Combine(modPath, "perf_report.txt")}");
            }
        }
        else
        {
            Output(PerformanceProfiler.GetBriefStatus());
        }
    }
}
