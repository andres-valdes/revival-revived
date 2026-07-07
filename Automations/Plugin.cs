using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace Automations;

/// <summary>
/// Automations: a factory-automation mod for Valheim. Placeable "machines" hold a
/// small item buffer and either produce, process, or store items; directional
/// "pipes" carry a machine's output into another machine's input; and a connector
/// (the wiring key) lets you lay those pipes and set blueprints. Chain them and a
/// self-running factory emerges -- Stockpile -> Kiln -> Smelter -> Assembler ->
/// Chest, all of it authoritative on ZDOs and synced across the network.
/// </summary>
[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public class Plugin : BaseUnityPlugin {
    public const string PluginGuid = "com.andres.automations";
    public const string PluginName = "Automations";
    public const string PluginVersion = "0.1.0";

    internal static new ManualLogSource Logger { get; private set; } = null!;

    // --- Simulation tuning ---------------------------------------------------
    private static ConfigEntry<float> TickIntervalCfg = null!;
    private static ConfigEntry<int> ProcessPerTickCfg = null!;
    private static ConfigEntry<int> TransferPerTickCfg = null!;

    /// <summary>Seconds between a machine's pipe tick (and the assembler's craft tick).</summary>
    public static float TickInterval => TickIntervalCfg?.Value ?? 1f;
    /// <summary>Blueprint batches the assembler crafts per tick.</summary>
    public static int ProcessPerTick => ProcessPerTickCfg?.Value ?? 3;
    /// <summary>Items a machine ships down each pipe per tick.</summary>
    public static int TransferPerTick => TransferPerTickCfg?.Value ?? 5;

    // --- Connector / rendering ----------------------------------------------
    /// <summary>Hold this key and press Use on a machine to lay/connect pipes.</summary>
    public static ConfigEntry<KeyCode> WiringKey = null!;
    /// <summary>Render pipes at all times, not only while the wiring key is held.</summary>
    public static ConfigEntry<bool> AlwaysShowPipes = null!;

    private Harmony? _harmony;

    private void Awake() {
        Logger = base.Logger;
        Logger.LogInfo($"{PluginName} v{PluginVersion} loaded!");

        TickIntervalCfg = Config.Bind("Simulation", "TickIntervalSeconds", 1f,
            new ConfigDescription("Seconds between each machine's pipe transfer tick.",
                new AcceptableValueRange<float>(0.1f, 10f)));
        ProcessPerTickCfg = Config.Bind("Simulation", "ProcessPerTick", 3,
            new ConfigDescription("Blueprint batches the assembler crafts per tick.",
                new AcceptableValueRange<int>(1, 50)));
        TransferPerTickCfg = Config.Bind("Simulation", "TransferPerTick", 5,
            new ConfigDescription("Items shipped down each pipe per tick.",
                new AcceptableValueRange<int>(1, 100)));

        WiringKey = Config.Bind("Connector", "WiringKey", KeyCode.LeftAlt,
            "Hold this key and press Use on a machine to start/connect a pipe. Alt-Use clears a machine's pipes.");
        AlwaysShowPipes = Config.Bind("Connector", "AlwaysShowPipes", false,
            "Draw pipe lines at all times, not only while the wiring key is held.");

        _harmony = Harmony.CreateAndPatchAll(typeof(Plugin).Assembly, PluginGuid);

#if DEBUG
        // Autonomous end-to-end test harness (opt-in via AUTO_E2E=1; the whole
        // E2E/ tree is compiled out of Release builds).
        if (System.Environment.GetEnvironmentVariable("AUTO_E2E") == "1") {
            E2E.E2ERunner.Bootstrap();
        }
#endif
    }

    private void OnDestroy() {
        _harmony?.UnpatchSelf();
    }
}
