using Automations.Components;
using Automations.Prefabs;
using HarmonyLib;

namespace Automations.Patches;

/// <summary>
/// Register the mod's machine prefabs (and the pipe renderer) on every client as
/// soon as the scene's prefab registry exists. Runs on hosts and joiners alike so
/// any peer can instantiate a machine directly from its replicated ZDO prefab hash.
/// </summary>
[HarmonyPatch(typeof(ZNetScene), "Awake")]
static class ZNetSceneAwakeRegisterPrefabsPatch {
    static void Postfix(ZNetScene __instance) {
        MachinePrefabs.RegisterAll(__instance);
        PipeRenderer.EnsureExists();
    }
}
