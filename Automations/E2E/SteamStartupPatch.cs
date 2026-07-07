using HarmonyLib;
using Steamworks;

namespace Automations.E2E;

/// <summary>
/// DEBUG/E2E-only launch fix. Valheim's Unity 6 update migrates PlatformPrefs
/// during very early startup (ZInput.Load / GuiScaler.Awake), and that migration
/// calls <see cref="SteamUtils.IsSteamRunningOnSteamDeck"/>. Launched directly
/// (outside Steam) as the harness does, Steamworks isn't initialized yet, so that
/// call throws "Steamworks is not initialized" and aborts the menu setup --
/// FejdStartup never appears and the harness stalls.
///
/// We short-circuit that one call to false (the harness never runs on a Steam
/// Deck). Applied in Plugin.Awake, before the game's scene Awakes; the whole E2E
/// tree (this included) is compiled out of Release.
/// </summary>
[HarmonyPatch(typeof(SteamUtils), nameof(SteamUtils.IsSteamRunningOnSteamDeck))]
static class SteamStartupPatch {
    static bool Prefix(ref bool __result) {
        __result = false;
        return false; // skip the original -> no Steamworks call
    }
}
