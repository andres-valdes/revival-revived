using HarmonyLib;
using ReviveAllies.Components;

namespace ReviveAllies.Patches;

/// <summary>
/// Intercepts CheckDeath (owner side) to introduce the downed state between
/// "health reaches 0" and "OnDeath fires." The down/expire decision lives here;
/// the downed state machine simply reacts to the replicated Downed flag this sets:
///  - not downed -> become downed, suppress death;
///  - downed, window expired and not being revived -> hand the spot to the real
///    grave, allow death;
///  - otherwise (downed, window active) -> suppress.
///
/// CheckDeath runs every frame in LateUpdate as
/// <c>if (!IsDead() &amp;&amp; GetHealth() &lt;= 0f) OnDeath();</c>.
/// </summary>
[HarmonyPatch(typeof(Character), "CheckDeath")]
static class CheckDeathPatch {
    static bool Prefix(Character __instance) {
        if (__instance is not Player player) return true;                    // players only
        if (!player.m_nview.IsOwner()) return true;                          // owner decides
        if (player.IsDead()) return true;                                    // already dead
        if (player.GetHealth() > 0f) return true;                            // nothing to do
        if (player.GetComponent<DownedStateMachine>() == null) return true;  // mod not attached yet

        if (!player.IsDowned()) {
            player.EnterDownedState(); // become downed; the machine reacts next tick
            return false;              // suppress vanilla death
        }

        bool channeling = player.GetComponent<ChannelSignal>().IsChanneling;
        if (!channeling && player.IsReviveWindowExpired()) {
            // Window ran out: hand the spot to the real grave and let vanilla OnDeath run.
            if (player.m_lastHit == null) {
                player.m_lastHit = new HitData { m_hitType = HitData.HitType.Self };
            }
            player.ExpireDownedState();
            return true; // allow vanilla OnDeath
        }

        return false; // downed, window active -> suppress; the machine drives it
    }
}
