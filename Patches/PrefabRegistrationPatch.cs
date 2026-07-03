using HarmonyLib;
using RevivalRevived.Components;

namespace RevivalRevived.Patches;

/// <summary>
/// Register the mod's networked prefabs on every client as soon as the scene's
/// prefab registry exists. Runs on hosts and joiners alike, so remote clients
/// can instantiate our objects directly from their ZDO prefab hash.
/// </summary>
[HarmonyPatch(typeof(ZNetScene), "Awake")]
static class ZNetSceneAwakeRegisterPrefabsPatch {
    static void Postfix(ZNetScene __instance) {
        DownedMarker.RegisterPrefab(__instance);
    }
}
