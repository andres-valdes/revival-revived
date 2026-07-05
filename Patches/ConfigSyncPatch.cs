using HarmonyLib;

namespace ReviveAllies.Patches;

/// <summary>
/// Replicates the revive config from the server (host) to every peer, so the
/// settings are server-authoritative. The host's values are the source of
/// truth; clients receive them and ignore their own local config for the
/// revive window / hold time / mode.
///
/// A global routed RPC (<see cref="Plugin.RpcConfig"/>) carries the three
/// values. The handler is registered once per session when the routed-RPC
/// layer comes up (<c>ZNet.Awake</c>), and the server pushes the current config
/// whenever a peer finishes connecting (<c>ZNet.RPC_PeerInfo</c>) and whenever
/// the host edits the config live (see the SettingChanged hooks in Plugin).
/// </summary>
[HarmonyPatch(typeof(ZNet), "Awake")]
static class ZNetAwakeConfigSyncPatch {
    static void Postfix() {
        Plugin.ResetServerConfig(); // fresh session: no server config yet

        ZRoutedRpc.instance?.Register<float, float, int>(Plugin.RpcConfig,
            (long sender, float window, float holdTime, int mode) => {
                // The server is authoritative from its own config; only clients adopt.
                if (ZNet.instance != null && ZNet.instance.IsServer()) return;
                Plugin.ApplyServerConfig(window, holdTime, mode != 0);
            });
    }
}

/// <summary>
/// When a peer finishes connecting, the server (re)broadcasts the config so the
/// newcomer -- and everyone else, idempotently -- has the authoritative values.
/// Runs on both ends; only the server actually sends.
/// </summary>
[HarmonyPatch(typeof(ZNet), "RPC_PeerInfo")]
static class ZNetPeerInfoConfigSyncPatch {
    static void Postfix(ZNet __instance) {
        if (__instance.IsServer()) Plugin.BroadcastConfig();
    }
}
