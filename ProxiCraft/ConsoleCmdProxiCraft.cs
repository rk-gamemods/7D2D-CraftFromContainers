using System;
using System.Collections.Generic;

namespace ProxiCraft;

/// <summary>
/// Console command for ProxiCraft diagnostics and troubleshooting.
/// Usage: pc [subcommand]
/// 
/// Subcommands:
///   status   - Show mod status and configuration
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
  diag       - Generate full diagnostic report
  test       - Test container scanning (shows nearby containers)
  reload     - Reload configuration from config.json
  toggle     - Toggle mod on/off
  conflicts  - Show detected mod conflicts
  debug      - Toggle debug logging

Examples:
  pc status
  pc diag
  pc test
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
}
