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

    /// <summary>Revive window duration in seconds (RR_E2E_WINDOW overrides for tests; Debug builds only).</summary>
    public static float ReviveWindow {
        get {
#if DEBUG
            if (s_windowEnvOverride > 0f) return s_windowEnvOverride;
#endif
            return ReviveWindowCfg?.Value ?? 30f;
        }
    }

#if DEBUG
    private static readonly float s_windowEnvOverride = ReadWindowOverride();

    private static float ReadWindowOverride() {
        var s = System.Environment.GetEnvironmentVariable("RR_E2E_WINDOW");
        return float.TryParse(s, out var v) && v > 0f ? v : 0f;
    }
#endif

    /// <summary>How long the channeled revive hold takes (Hold mode).</summary>
    public static float ReviveDuration => UnityEngine.Mathf.Max(0.1f, ReviveHoldTimeCfg?.Value ?? 4f);

    /// <summary>True when a single press (no hold) completes the revive.</summary>
    public static bool RevivePressMode =>
        ReviveModeCfg != null && ReviveModeCfg.Value == ReviveModeType.Press;

    /// <summary>Health fraction restored on revive (0-1).</summary>
    public const float ReviveHealthFraction = 0.25f;

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

#if DEBUG
        // Autonomous end-to-end test harness (opt-in via RR_E2E=1; the whole
        // E2E/ tree is compiled out of Release builds).
        if (System.Environment.GetEnvironmentVariable("RR_E2E") == "1") {
            E2E.E2ERunner.Bootstrap();
        }
#endif
    }

    private void OnDestroy() {
        _harmony?.UnpatchSelf();
    }
}
