using System.Collections.Generic;

namespace ProxiCraft;

/// <summary>
/// Configuration settings for the ProxiCraft mod.
/// These are loaded from config.json in the mod's folder.
/// </summary>
public class ModConfig
{
    /// <summary>Enable or disable the entire mod</summary>
    public bool modEnabled = true;

    /// <summary>Enable debug logging to the console (disable in production for performance)</summary>
    public bool isDebug = false;

    /// <summary>
    /// Show verbose startup health check output even when everything is OK.
    /// When false (default), health check is silent unless there are issues.
    /// Set to true to always see full health check output for diagnostics.
    /// </summary>
    public bool verboseHealthCheck = false;

    /// <summary>
    /// Maximum range in blocks/meters to search for containers.
    /// Default is 15 blocks (same floor/area).
    /// Set to -1 or 0 for unlimited range (searches all loaded chunks - not recommended).
    /// </summary>
    public float range = 15f;

    // ===========================================
    // STORAGE SOURCES - What containers to search
    // ===========================================

    /// <summary>Include vehicle storage in the search for items</summary>
    public bool pullFromVehicles = true;

    /// <summary>Include player's drone storage in the search for items</summary>
    public bool pullFromDrones = true;

    /// <summary>Include dew collector contents in the search for items</summary>
    public bool pullFromDewCollectors = true;

    /// <summary>Include workstation output slots (forge, campfire, etc.) in the search for items</summary>
    public bool pullFromWorkstationOutputs = true;

    /// <summary>Allow using items from locked containers you have access to</summary>
    public bool allowLockedContainers = true;

    // ===========================================
    // STORAGE PRIORITY - Order to search containers
    // ===========================================

    /// <summary>
    /// Priority order for searching storage sources. Lower numbers = checked first.
    /// Items are pulled from sources in this order until the required amount is found.
    /// 
    /// Valid keys: Drone, DewCollector, Workstation, Container, Vehicle
    /// Values can be numbers (1, 2, 3) or letters (A, B, C) - sorted alphanumerically.
    /// 
    /// Default order matches Beyond Storage 2:
    ///   1. Drones, 2. Dew Collectors, 3. Workstations, 4. Containers, 5. Vehicles
    /// 
    /// Note: Player backpack and toolbelt are ALWAYS checked first (vanilla behavior).
    /// </summary>
    public Dictionary<string, string> storagePriority = new()
    {
        { "Drone", "1" },
        { "DewCollector", "2" },
        { "Workstation", "3" },
        { "Container", "4" },
        { "Vehicle", "5" }
    };

    // ===========================================
    // FEATURES - What actions can use container items
    // ===========================================

    /// <summary>Allow crafting recipes to use items from containers</summary>
    public bool enableForCrafting = true;

    /// <summary>Allow reloading weapons from nearby containers</summary>
    public bool enableForReload = true;

    /// <summary>Allow refueling vehicles from nearby containers</summary>
    public bool enableForRefuel = true;

    /// <summary>Allow using currency from containers for trader purchases</summary>
    public bool enableForTrader = true;

    /// <summary>Allow crafting from containers for repair and upgrade operations</summary>
    public bool enableForRepairAndUpgrade = true;

    /// <summary>Allow quest objectives to count items in nearby containers</summary>
    public bool enableForQuests = true;

    /// <summary>Allow painting blocks using paint from nearby containers</summary>
    public bool enableForPainting = true;

    /// <summary>Allow lockpicking using lockpicks from nearby containers</summary>
    public bool enableForLockpicking = true;

    /// <summary>Allow refueling generators/power sources from nearby containers</summary>
    public bool enableForGeneratorRefuel = true;

    /// <summary>Allow repairing items (weapons/tools) using repair kits from nearby containers</summary>
    public bool enableForItemRepair = true;

    /// <summary>Show total ammo from nearby containers in HUD stat bar</summary>
    public bool enableHudAmmoCounter = true;

    /// <summary>Enable real-time recipe tracker updates when container items change</summary>
    public bool enableRecipeTrackerUpdates = true;

    // ===========================================    // EXPERIMENTAL FEATURES - Enhanced Safety Mode
    // ===========================================
    // These features implement the "Count Spoofing" / SCVI architecture.
    // They are designed to improve multiplayer stability but are NOT required
    // for single-player use. All default to FALSE - when disabled, the mod
    // works exactly as it did before these features were added.
    //
    // TECHNICAL: "Enhanced Safety" is the user-facing name for what's internally
    // called "Count Spoofing" or "SCVI" (Subsystem-Centric Virtual Inventory).
    // ===========================================

    /// <summary>
    /// WARNING: These features are EXPERIMENTAL. Not needed for single-player.
    /// Enable only if experiencing crashes in multiplayer.
    /// When all flags are false, the mod works exactly as before.
    /// </summary>
    public string experimentalFeatures_WARNING = "EXPERIMENTAL - Enable only if needed for multiplayer stability";

    /// <summary>
    /// Use enhanced safety mode for crafting operations.
    /// When enabled, uses VirtualInventoryProvider for safer item consumption.
    /// </summary>
    public bool enhancedSafetyCrafting = false;

    /// <summary>
    /// Use enhanced safety mode for reload operations.
    /// When enabled, uses VirtualInventoryProvider for safer ammo consumption.
    /// </summary>
    public bool enhancedSafetyReload = false;

    /// <summary>
    /// Use enhanced safety mode for block repair/upgrade operations.
    /// When enabled, uses VirtualInventoryProvider for safer material consumption.
    /// </summary>
    public bool enhancedSafetyRepair = false;

    /// <summary>
    /// Use enhanced safety mode for vehicle repair operations.
    /// When enabled, uses VirtualInventoryProvider for safer repair kit consumption.
    /// </summary>
    public bool enhancedSafetyVehicle = false;

    /// <summary>
    /// Use enhanced safety mode for refueling operations (vehicles and generators).
    /// When enabled, uses VirtualInventoryProvider for safer fuel consumption.
    /// </summary>
    public bool enhancedSafetyRefuel = false;

    /// <summary>
    /// Enable diagnostic logging for enhanced safety features.
    /// Writes detailed logs to help debug issues with experimental features.
    /// </summary>
    public bool enhancedSafetyDiagnosticLogging = false;

    // ===========================================    // MULTIPLAYER SAFETY - Crash prevention
    // ===========================================

    /// <summary>
    /// Enable immediate safety lock when clients connect in multiplayer.
    /// When true (default): Mod IMMEDIATELY locks when any client connects, unlocks after handshake.
    /// When false: Trust that all clients have ProxiCraft (honor system - NOT RECOMMENDED).
    /// 
    /// WARNING: Setting this to false can cause server-wide CTD if ANY player joins without ProxiCraft!
    /// Only disable on heavily-moderated servers where mod installation is enforced externally.
    /// </summary>
    public bool multiplayerImmediateLock = true;

    /// <summary>
    /// Timeout in seconds to wait for client handshake before confirming they don't have ProxiCraft.
    /// Lower values = faster detection of non-ProxiCraft clients, but may false-positive on slow connections.
    /// Default: 10 seconds (generous for slow connections).
    /// Minimum: 3 seconds. Maximum: 30 seconds.
    /// 
    /// Note: This only affects how long until the "CULPRIT" message appears.
    /// If multiplayerImmediateLock is true, the mod is already locked before this timeout.
    /// </summary>
    public float multiplayerHandshakeTimeoutSeconds = 10f;

    /// <summary>
    /// Time in seconds before a container lock automatically expires.
    /// Prevents "ghost locks" if unlock packets are lost or players disconnect ungracefully.
    /// Default: 300 seconds (5 minutes) - long enough for normal use, short enough to self-heal.
    /// Set to 0 to disable expiration (not recommended).
    /// </summary>
    public float containerLockExpirySeconds = 300f;

    // ===========================================
    // DEPRECATED/REMOVED FEATURES
    // ===========================================

    /// <summary>
    /// REMOVED: This feature was removed due to item duplication bugs.
    /// See TRADER_SELLING_POSTMORTEM.md for details.
    /// Kept for config file backward compatibility (ignored).
    /// </summary>
    public bool enableTraderSelling = false;

    /// <summary>
    /// Respect user-locked slots in containers (locked slots are excluded from all operations).
    /// When true (default), items in locked slots won't be counted, used, or removed.
    /// </summary>
    public bool respectLockedSlots = true;
}
