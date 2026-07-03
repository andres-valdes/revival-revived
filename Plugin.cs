using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace RevivalRevived;

/// <summary>How a reviver triggers a revive on a downed player's marker.</summary>
public enum ReviveModeType {
    /// <summary>Keep the interact key held for HoldTimeSeconds.</summary>
    Hold,
    /// <summary>A single press revives immediately.</summary>
    Press,
}

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public class Plugin : BaseUnityPlugin {
    public const string PluginGuid = "com.andres.revivalrevived";
    public const string PluginName = "RevivalRevived";
    public const string PluginVersion = "0.1.0";

    internal static new ManualLogSource Logger { get; private set; } = null!;

    // Config (authoritative on the downed player's owner for timing; the
    // reviver's own config only affects which button gesture it accepts).
    internal static ConfigEntry<ReviveModeType> ReviveModeCfg = null!;
    internal static ConfigEntry<float> ReviveHoldTimeCfg = null!;
    internal static ConfigEntry<float> ReviveWindowCfg = null!;

    private Harmony? _harmony;

    private void Awake() {
        Logger = base.Logger;
        Logger.LogInfo($"{PluginName} v{PluginVersion} loaded!");

        ReviveModeCfg = Config.Bind("Revive", "Mode", ReviveModeType.Hold,
            "Hold: the interact key must be held for HoldTimeSeconds to revive. Press: a single press revives instantly.");
        ReviveHoldTimeCfg = Config.Bind("Revive", "HoldTimeSeconds", 4f,
            new ConfigDescription("How long the interact key must be held to complete a revive (Hold mode).",
                new AcceptableValueRange<float>(0.1f, 60f)));
        ReviveWindowCfg = Config.Bind("Revive", "WindowSeconds", 30f,
            new ConfigDescription("How long a downed player can be revived before dying for real.",
                new AcceptableValueRange<float>(5f, 600f)));

        _harmony = Harmony.CreateAndPatchAll(typeof(Plugin).Assembly, PluginGuid);

        // Autonomous end-to-end test harness (opt-in via RR_E2E=1).
        if (System.Environment.GetEnvironmentVariable("RR_E2E") == "1") {
            E2E.E2ERunner.Bootstrap();
        }
    }

    private void OnDestroy() {
        _harmony?.UnpatchSelf();
    }
}
