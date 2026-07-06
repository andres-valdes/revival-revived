using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace ReviveAllies;

/// <summary>How a reviver triggers a revive on a downed player's marker.</summary>
public enum ReviveModeType {
    /// <summary>Keep the interact key held for HoldTimeSeconds.</summary>
    Hold,
    /// <summary>A single press revives immediately.</summary>
    Press,
}

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public class Plugin : BaseUnityPlugin {
    public const string PluginGuid = "com.andres.reviveallies";
    public const string PluginName = "ReviveAllies";
    public const string PluginVersion = "0.3.2";

    internal static new ManualLogSource Logger { get; private set; } = null!;

    // Config is SERVER-AUTHORITATIVE. The host's values are the source of truth
    // and are replicated to every peer (see ConfigSyncPatch); a connected client
    // ignores its own config for these and uses what the server sent. On the
    // host / in single-player the local config IS the authoritative one.
    internal static ConfigEntry<ReviveModeType> ReviveModeCfg = null!;
    internal static ConfigEntry<float> ReviveHoldTimeCfg = null!;
    internal static ConfigEntry<float> ReviveWindowCfg = null!;

    /// <summary>Routed-RPC name carrying the server config to peers.</summary>
    internal const string RpcConfig = "ReviveAllies_Config";

    // Server config as received by a client (valid only when s_hasServerConfig).
    private static bool s_hasServerConfig;
    private static float s_srvWindow;
    private static float s_srvHoldTime;
    private static bool s_srvPressMode;

    /// <summary>True when we should use the server-replicated config: a connected client, not the server.</summary>
    private static bool UseServerConfig =>
        s_hasServerConfig && ZNet.instance != null && !ZNet.instance.IsServer();

    /// <summary>Revive window duration in seconds (RR_E2E_WINDOW overrides for tests; Debug builds only).</summary>
    public static float ReviveWindow {
        get {
#if DEBUG
            if (s_windowEnvOverride > 0f) return s_windowEnvOverride;
#endif
            return UseServerConfig ? s_srvWindow : (ReviveWindowCfg?.Value ?? 30f);
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
    public static float ReviveDuration =>
        UnityEngine.Mathf.Max(0.1f, UseServerConfig ? s_srvHoldTime : (ReviveHoldTimeCfg?.Value ?? 4f));

    /// <summary>True when a single press (no hold) completes the revive.</summary>
    public static bool RevivePressMode =>
        UseServerConfig ? s_srvPressMode : (ReviveModeCfg != null && ReviveModeCfg.Value == ReviveModeType.Press);

    /// <summary>Health fraction restored on revive (0-1).</summary>
    public const float ReviveHealthFraction = 0.25f;

    /// <summary>Client: adopt the revive config the server sent.</summary>
    internal static void ApplyServerConfig(float window, float holdTime, bool pressMode) {
        s_srvWindow = window;
        s_srvHoldTime = holdTime;
        s_srvPressMode = pressMode;
        s_hasServerConfig = true;
        Logger.LogInfo($"Adopted server revive config: window={window:F0}s hold={holdTime:F1}s press={pressMode}");
    }

    /// <summary>Drop any replicated config (new session / left the server).</summary>
    internal static void ResetServerConfig() => s_hasServerConfig = false;

    /// <summary>Server: replicate the authoritative config to every peer. No-op off the server.</summary>
    internal static void BroadcastConfig() {
        if (ZNet.instance == null || !ZNet.instance.IsServer() || ZRoutedRpc.instance == null) return;
        ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, RpcConfig,
            ReviveWindowCfg?.Value ?? 30f,
            UnityEngine.Mathf.Max(0.1f, ReviveHoldTimeCfg?.Value ?? 4f),
            (ReviveModeCfg?.Value == ReviveModeType.Press) ? 1 : 0);
    }

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
            new ConfigDescription("How long a downed player can be revived before dying for real. Server-authoritative: the host's value governs everyone.",
                new AcceptableValueRange<float>(5f, 600f)));

        // Live edits on the host re-replicate to peers (no-op on a client).
        ReviveModeCfg.SettingChanged += (_, _) => BroadcastConfig();
        ReviveHoldTimeCfg.SettingChanged += (_, _) => BroadcastConfig();
        ReviveWindowCfg.SettingChanged += (_, _) => BroadcastConfig();

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
