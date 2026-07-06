using HarmonyLib;
using ReviveAllies.Components;

namespace ReviveAllies.Patches;

/// <summary>
/// A downed player is helpless, so enemies should leave them alone.
///
/// <see cref="BaseAI.IsEnemy(Character, Character)"/> is the faction-hostility
/// check every targeting path funnels through: <c>FindEnemy</c> uses it when
/// acquiring a new target, and the instance <c>IsEnemy</c> overload delegates
/// to this static one. Denying it for a downed player therefore stops new aggro
/// outright and -- because MonsterAI re-runs target selection on an interval --
/// makes an already-engaged enemy drop the player within a couple of seconds of
/// them going down. Once revived (no longer downed) they are a valid target
/// again immediately.
/// </summary>
[HarmonyPatch(typeof(BaseAI), nameof(BaseAI.IsEnemy), new[] { typeof(Character), typeof(Character) })]
static class BaseAIIgnoreDownedPatch {
    static void Postfix(Character a, Character b, ref bool __result) {
        if (__result && (IsDownedPlayer(a) || IsDownedPlayer(b))) {
            __result = false;
        }
    }

    static bool IsDownedPlayer(Character c) => c is Player p && p.IsDowned();
}
