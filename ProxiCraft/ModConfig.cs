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

    /// <summary>Allow crafting from containers for repair and upgrade operations</summary>
    public bool enableForRepairAndUpgrade = true;

    /// <summary>Allow using currency from containers for trader purchases</summary>
    public bool enableForTrader = true;

    /// <summary>Allow refueling vehicles from nearby containers</summary>
    public bool enableForRefuel = true;

    /// <summary>Allow reloading weapons from nearby containers</summary>
    public bool enableForReload = true;

    /// <summary>Allow quest objectives to count items in nearby containers</summary>
    public bool enableForQuests = true;

    /// <summary>Include vehicle storage in the search for items</summary>
    public bool enableFromVehicles = true;

    /// <summary>Allow using items from locked containers you have access to</summary>
    public bool allowLockedContainers = true;

    /// <summary>
    /// Maximum range in blocks/meters to search for containers.
    /// Default is 15 blocks (same floor/area).
    /// Set to -1 or 0 for unlimited range (searches all loaded chunks - not recommended).
    /// </summary>
    public float range = 15f;
}
