using BepInEx;
using BepInEx.Logging;
using HarmonyLib;

namespace RevivalRevived;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public class Plugin : BaseUnityPlugin {
    public const string PluginGuid = "com.andres.revivalrevived";
    public const string PluginName = "RevivalRevived";
    public const string PluginVersion = "0.1.0";

    internal static new ManualLogSource Logger { get; private set; } = null!;

    private Harmony? _harmony;

    private void Awake() {
        Logger = base.Logger;
        Logger.LogInfo($"{PluginName} v{PluginVersion} loaded!");

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
