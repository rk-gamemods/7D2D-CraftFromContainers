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

    // ===========================================
    // DEPRECATED - Kept for backward compatibility
    // ===========================================

    /// <summary>DEPRECATED: Use pullFromVehicles instead. Kept for config migration.</summary>
    public bool? enableFromVehicles = null;

    /// <summary>
    /// Called after deserialization to migrate old config values to new names.
    /// </summary>
    public void MigrateDeprecatedSettings()
    {
        // Migrate enableFromVehicles -> pullFromVehicles
        if (enableFromVehicles.HasValue)
        {
            pullFromVehicles = enableFromVehicles.Value;
            enableFromVehicles = null; // Clear so it doesn't get saved
        }
    }
}
