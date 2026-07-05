using HarmonyLib;
using ReviveAllies.Components;

namespace ReviveAllies.Patches;

/// <summary>
/// Intercepts CheckDeath (owner side) to introduce the downed state between
/// "health reaches 0" and "OnDeath fires." The decision is delegated to
/// <see cref="DownedController.HandleZeroHealth"/> so the down/expire logic
/// lives in one place; this patch is just the hook.
///
/// CheckDeath runs every frame in LateUpdate as
/// <c>if (!IsDead() &amp;&amp; GetHealth() &lt;= 0f) OnDeath();</c>.
/// </summary>
[HarmonyPatch(typeof(Character), "CheckDeath")]
static class CheckDeathPatch {
    static bool Prefix(Character __instance) {
        if (__instance is not Player player) return true;      // players only
        if (!player.m_nview.IsOwner()) return true;            // owner decides
        if (player.IsDead()) return true;                      // already dead
        if (player.GetHealth() > 0f) return true;              // nothing to do

        var controller = player.GetComponent<DownedController>();
        if (controller == null) return true; // no controller yet: vanilla death

        // false -> suppress vanilla death (entering/holding downed);
        // true  -> allow vanilla OnDeath (the window expired).
        return controller.HandleZeroHealth();
    }
}
