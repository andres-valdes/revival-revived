using HarmonyLib;
using RevivalRevived.Components;

namespace RevivalRevived.Patches;

/// <summary>
/// Intercepts CheckDeath to introduce the downed state between
/// "health reaches 0" and "OnDeath fires."
///
/// CheckDeath runs every frame in LateUpdate:
///   if (!IsDead() && GetHealth() <= 0f) OnDeath();
///
/// We prefix it to:
///   - First time health <= 0: enter downed state, skip OnDeath
///   - While downed and window active: keep skipping OnDeath
///   - When window expires: clear downed, let OnDeath proceed naturally
/// </summary>
[HarmonyPatch(typeof(Character), "CheckDeath")]
static class CheckDeathPatch {
    static bool Prefix(Character __instance) {
        // Only intercept for players
        if (__instance is not Player player) return true;

        // Let vanilla handle it if not the network owner
        if (!player.m_nview.IsOwner()) return true;

        // If already truly dead (s_dead set), don't interfere
        if (player.IsDead()) return true;

        // If health is fine, nothing to do
        if (player.GetHealth() > 0f) return true;

        // --- Health is <= 0 and player is not dead ---

        if (DownedState.IsDowned(player)) {
            // Already downed -- check if window expired
            if (DownedState.IsReviveWindowExpired(player)) {
                // Window expired: clear downed state, let OnDeath fire.
                // Defensive: Player.OnDeath dereferences m_lastHit (switch on
                // m_lastHit.m_hitType) before spawning the tombstone, so a death
                // with no recorded hit would NRE and skip the grave. In normal
                // play the downing damage sets m_lastHit; guard the edge case.
                if (player.m_lastHit == null) {
                    player.m_lastHit = new HitData { m_hitType = HitData.HitType.Self };
                }
                DownedState.ExpireDownedState(player);
                return true; // proceed to OnDeath
            }

            // Still within window -- suppress OnDeath
            return false;
        }

        // Not downed yet -- enter downed state instead of dying
        DownedState.EnterDownedState(player);
        return false; // suppress OnDeath
    }
}
