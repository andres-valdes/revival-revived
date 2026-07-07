using Automations.Components;
using Automations.Prefabs;
using HarmonyLib;

namespace Automations.Patches;

/// <summary>
/// Once the scene's prefab registry exists, lift the pipe-connection VFX and make
/// sure the pipe renderer is alive. Machines themselves need no registration -- they
/// are vanilla pieces that the attach patch turns into machines on Awake.
/// </summary>
[HarmonyPatch(typeof(ZNetScene), "Awake")]
static class ZNetSceneAwakeSetupPatch {
    static void Postfix(ZNetScene __instance) {
        MachinePrefabs.RegisterAll(__instance);
        PipeRenderer.EnsureExists();
    }
}
