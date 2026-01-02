using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;

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

Config Commands:
  pc config list              - List all settings with current values
  pc config get <setting>     - Get a specific setting value
  pc config set <setting> <v> - Set a setting value (temporary)
  pc config save              - Save current settings to config.json
  pc set <setting> <value>    - Shortcut for config set
  pc get <setting>            - Shortcut for config get

Multiplayer Commands:
  pc mp                       - Show multiplayer mod status
  pc multiplayer              - Show multiplayer mod status

Performance Commands:
  pc perf          - Show brief performance status
  pc perf on       - Start profiling (collects timing data)
  pc perf off      - Stop profiling
  pc perf reset    - Clear profiling data
  pc perf report   - Show detailed performance report

Examples:
  pc status
  pc config list
  pc set range 30
  pc config save
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

                case "mp":
                case "multiplayer":
                    ShowMultiplayerStatus();
                    break;

                case "perf":
                case "performance":
                    HandlePerfCommand(_params);
                    break;

                case "config":
                    HandleConfigCommand(_params);
                    break;

                case "set":
                    // Shortcut: pc set <setting> <value>
                    if (_params.Count >= 3)
                        SetSetting(_params[1], _params[2]);
                    else
                        Output("Usage: pc set <setting> <value>");
                    break;

                case "get":
                    // Shortcut: pc get <setting>
                    if (_params.Count >= 2)
                        GetSetting(_params[1]);
                    else
                        Output("Usage: pc get <setting>");
                    break;

                case "help":
                case "?":
                    Output(getHelp());
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
        
        // Show immediate lock status (highest priority - "Guilty Until Proven Innocent")
        if (MultiplayerModTracker.IsImmediatelyLocked)
        {
            if (MultiplayerModTracker.IsHostSafetyLockTriggered)
            {
                OutputWarning($"  Multiplayer: LOCKED - Player '{MultiplayerModTracker.HostLockCulprit}' CONFIRMED without ProxiCraft!");
                Output($"    Mod disabled for all players to prevent crashes.");
                Output($"    Ask '{MultiplayerModTracker.HostLockCulprit}' to install ProxiCraft or kick them.");
            }
            else
            {
                OutputWarning($"  Multiplayer: LOCKED - Verifying client(s)...");
                Output($"    Unverified clients: {MultiplayerModTracker.UnverifiedClientCount}");
                Output($"    Mod locked until all clients confirm ProxiCraft installation.");
            }
        }
        // Show host-side safety lock status (confirmed bad client)
        else if (MultiplayerModTracker.IsHostSafetyLockTriggered)
        {
            OutputWarning($"  Multiplayer: DISABLED - Player '{MultiplayerModTracker.HostLockCulprit}' missing ProxiCraft!");
            Output($"    Mod disabled for all players to prevent crashes.");
            Output($"    Ask '{MultiplayerModTracker.HostLockCulprit}' to install ProxiCraft or kick them.");
        }
        // Show hosting status
        else if (MultiplayerModTracker.IsHosting)
        {
            var safetyStatus = MultiplayerModTracker.IsImmediateLockConfigEnabled ? "safety ON" : "⚠️ SAFETY OFF";
            Output($"  Multiplayer: HOSTING (all clients verified ✓) [{safetyStatus}]");
        }
        // Show client-side multiplayer safety lock status
        else if (MultiplayerModTracker.IsMultiplayerSession)
        {
            if (MultiplayerModTracker.IsMultiplayerUnlocked)
            {
                Output($"  Multiplayer: UNLOCKED (server confirmed)");
            }
            else if (MultiplayerModTracker.IsWaitingForServer)
            {
                OutputWarning($"  Multiplayer: LOCKED (waiting for server...)");
            }
            else
            {
                var reason = MultiplayerModTracker.GetLockReason();
                OutputWarning($"  Multiplayer: LOCKED - {reason ?? "unknown"}");
            }
        }
        
        Output($"  Debug Mode: {(config?.isDebug == true ? "ON" : "OFF")}");
        Output($"  Verbose Health Check: {(config?.verboseHealthCheck == true ? "ON" : "OFF")}");
        Output("");
        Output("  Features:");
        Output($"    Crafting: {GetFeatureStatus(config?.modEnabled)}");
        Output($"    Repair/Upgrade: {GetFeatureStatus(config?.enableForRepairAndUpgrade)}");
        Output($"    Trader: {GetFeatureStatus(config?.enableForTrader)}");
        Output($"    Reload: {GetFeatureStatus(config?.enableForReload)}");
        Output($"    Refuel: {GetFeatureStatus(config?.enableForRefuel)}");
        Output($"    Vehicle Storage: {GetFeatureStatus(config?.pullFromVehicles)}");
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
        
        // Add scan method diagnostics
        Output("");
        Output("=== Entity Scan Method ===");
        Output(ContainerManager.GetScanMethodDiagnostics(ProxiCraft.Config));
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
            ProxiCraft.ReloadConfig();
            Output("Configuration reloaded from config.json");
            Output("");
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

    private void ShowMultiplayerStatus()
    {
        Output("=== Multiplayer Mod Status ===");

        if (!SingletonMonoBehaviour<ConnectionManager>.Instance.IsConnected)
        {
            Output("  Not in multiplayer session.");
            return;
        }

        bool isServer = SingletonMonoBehaviour<ConnectionManager>.Instance.IsServer;
        Output($"  Role: {(isServer ? "Server/Host" : "Client")}");
        Output($"  Local mod: {ProxiCraft.MOD_NAME} v{ProxiCraft.MOD_VERSION}");
        Output("");

        var trackedPlayers = MultiplayerModTracker.GetTrackedPlayers();
        if (trackedPlayers.Count == 0)
        {
            Output("  No remote players tracked yet.");
            Output("  (Players are tracked when they send a ProxiCraft handshake)");
            return;
        }

        Output($"  Tracked players: {trackedPlayers.Count}");
        Output("");

        bool hasConflicts = false;
        foreach (var kvp in trackedPlayers)
        {
            var info = kvp.Value;
            bool isConflict = info.ModName != ProxiCraft.MOD_NAME;

            if (isConflict)
            {
                hasConflicts = true;
                OutputWarning($"  [CONFLICT] {info.PlayerName}:");
                OutputWarning($"    Using: {info.ModName} v{info.ModVersion}");
            }
            else
            {
                string versionMatch = info.ModVersion == ProxiCraft.MOD_VERSION ? "OK" : "VERSION MISMATCH";
                Output($"  [OK] {info.PlayerName}: {info.ModName} v{info.ModVersion} ({versionMatch})");
            }
        }

        if (hasConflicts)
        {
            Output("");
            OutputWarning("  CONFLICTS DETECTED!");
            OutputWarning("  Different container mods between players can cause CTD when");
            OutputWarning("  interacting with containers or workstations.");
            OutputWarning("  All players should use the same container mod.");
        }
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

    #region Config Commands

    private void HandleConfigCommand(List<string> args)
    {
        if (args.Count < 2)
        {
            ShowConfigHelp();
            return;
        }

        string subCmd = args[1].ToLowerInvariant();
        switch (subCmd)
        {
            case "list":
                ListAllSettings();
                break;
            case "get":
                if (args.Count >= 3)
                    GetSetting(args[2]);
                else
                    Output("Usage: pc config get <setting>");
                break;
            case "set":
                if (args.Count >= 4)
                    SetSetting(args[2], args[3]);
                else
                    Output("Usage: pc config set <setting> <value>");
                break;
            case "save":
                SaveConfig();
                break;
            case "reset":
                ResetConfigToDefaults(args.Count >= 3 && args[2].ToLowerInvariant() == "confirm");
                break;
            default:
                ShowConfigHelp();
                break;
        }
    }

    private void ShowConfigHelp()
    {
        Output("Config Commands:");
        Output("  pc config list              - List all settings with current values");
        Output("  pc config get <setting>     - Get a specific setting value");
        Output("  pc config set <setting> <v> - Set a setting value (temporary)");
        Output("  pc config save              - Save current settings to config.json");
        Output("  pc config reset confirm     - Reset all settings to defaults");
        Output("");
        Output("Shortcuts:");
        Output("  pc set <setting> <value>    - Same as 'pc config set'");
        Output("  pc get <setting>            - Same as 'pc config get'");
    }

    private void ListAllSettings()
    {
        var config = ProxiCraft.Config;
        if (config == null)
        {
            OutputError("Configuration not loaded!");
            return;
        }

        Output("=== ProxiCraft Configuration ===");
        Output("");
        Output("[General]");
        Output($"  modEnabled = {config.modEnabled}");
        Output($"  isDebug = {config.isDebug}");
        Output($"  verboseHealthCheck = {config.verboseHealthCheck}");
        Output($"  range = {config.range}");
        Output("");
        Output("[Storage Sources]");
        Output($"  pullFromVehicles = {config.pullFromVehicles}");
        Output($"  pullFromDrones = {config.pullFromDrones}");
        Output($"  pullFromDewCollectors = {config.pullFromDewCollectors}");
        Output($"  pullFromWorkstationOutputs = {config.pullFromWorkstationOutputs}");
        Output($"  allowLockedContainers = {config.allowLockedContainers}");
        Output("");
        Output("[Features]");
        Output($"  enableForCrafting = {config.enableForCrafting}");
        Output($"  enableForReload = {config.enableForReload}");
        Output($"  enableForRefuel = {config.enableForRefuel}");
        Output($"  enableForTrader = {config.enableForTrader}");
        Output($"  enableForQuests = {config.enableForQuests}");
        Output($"  enableForPainting = {config.enableForPainting}");
        Output($"  enableForLockpicking = {config.enableForLockpicking}");
        Output($"  enableForGeneratorRefuel = {config.enableForGeneratorRefuel}");
        Output($"  enableForItemRepair = {config.enableForItemRepair}");
        Output($"  enableForRepairAndUpgrade = {config.enableForRepairAndUpgrade}");
        Output("");
        Output("[New Features]");
        Output($"  enableHudAmmoCounter = {config.enableHudAmmoCounter}");
        Output($"  enableRecipeTrackerUpdates = {config.enableRecipeTrackerUpdates}");
        Output($"  respectLockedSlots = {config.respectLockedSlots}");
    }

    private void GetSetting(string settingName)
    {
        var config = ProxiCraft.Config;
        if (config == null)
        {
            OutputError("Configuration not loaded!");
            return;
        }

        var field = typeof(ModConfig).GetField(settingName,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

        if (field == null)
        {
            OutputError($"Unknown setting: {settingName}");
            Output("Use 'pc config list' to see all available settings.");
            return;
        }

        var value = field.GetValue(config);
        Output($"{field.Name} = {value}");
    }

    private void SetSetting(string settingName, string valueStr)
    {
        var config = ProxiCraft.Config;
        if (config == null)
        {
            OutputError("Configuration not loaded!");
            return;
        }

        var field = typeof(ModConfig).GetField(settingName,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

        if (field == null)
        {
            OutputError($"Unknown setting: {settingName}");
            Output("Use 'pc config list' to see all available settings.");
            return;
        }

        try
        {
            object newValue;
            if (field.FieldType == typeof(bool))
            {
                newValue = bool.Parse(valueStr);
            }
            else if (field.FieldType == typeof(float))
            {
                newValue = float.Parse(valueStr, System.Globalization.CultureInfo.InvariantCulture);
            }
            else if (field.FieldType == typeof(int))
            {
                newValue = int.Parse(valueStr);
            }
            else if (field.FieldType == typeof(string))
            {
                newValue = valueStr;
            }
            else
            {
                OutputError($"Unsupported setting type: {field.FieldType.Name}");
                return;
            }

            var oldValue = field.GetValue(config);
            field.SetValue(config, newValue);

            Output($"{field.Name}: {oldValue} -> {newValue}");
            Output("Note: Use 'pc config save' to persist this change.");

            // Invalidate caches for certain settings
            if (settingName.ToLowerInvariant() == "range")
            {
                ContainerManager.ClearCache();
                Output("Container cache cleared due to range change.");
            }
        }
        catch (Exception ex)
        {
            OutputError($"Failed to set {settingName}: {ex.Message}");
            Output($"Expected type: {field.FieldType.Name}");
        }
    }

    private void SaveConfig()
    {
        try
        {
            string configPath = ProxiCraft.GetConfigPath();

            string json = JsonConvert.SerializeObject(ProxiCraft.Config, Formatting.Indented);
            File.WriteAllText(configPath, json);

            Output($"Configuration saved to: {configPath}");
        }
        catch (Exception ex)
        {
            OutputError($"Failed to save config: {ex.Message}");
        }
    }

    private void ResetConfigToDefaults(bool confirmed)
    {
        if (!confirmed)
        {
            Output("This will reset all settings to defaults.");
            Output("This change is NOT saved until you run 'pc config save'.");
            Output("Use 'pc config reset confirm' to confirm.");
            return;
        }

        // Create a new default config
        var defaultConfig = new ModConfig();

        // Copy all field values from default to current config
        foreach (var field in typeof(ModConfig).GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            var defaultValue = field.GetValue(defaultConfig);
            field.SetValue(ProxiCraft.Config, defaultValue);
        }

        Output("All settings reset to defaults.");
        Output("Use 'pc config save' to persist these changes.");
        Output("");
        ListAllSettings();
    }

    #endregion
}
