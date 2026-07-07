using Automations.Components;
using HarmonyLib;
using UnityEngine;

namespace Automations.Patches;

/// <summary>
/// The connector. While the wiring key is held, pressing Use on a machine lays a
/// pipe instead of doing the piece's normal thing (opening a chest, adding ore):
/// the first press picks the pipe's source, the second connects it to a target.
/// Alt-Use clears a machine's outgoing pipes. Releasing the key restores normal
/// interaction. Pipes are drawn by <see cref="PipeRenderer"/> whenever the key is
/// held.
///
/// This intercepts the game's single interaction entry point
/// (<c>Player.Interact</c>), so it composes with vanilla behaviour cleanly rather
/// than adding a competing Interactable.
/// </summary>
[HarmonyPatch(typeof(Player), "Interact", new[] { typeof(GameObject), typeof(bool), typeof(bool) })]
static class PlayerInteractWiringPatch {
    static bool Prefix(Player __instance, GameObject go, bool hold, bool alt) {
        if (__instance != Player.m_localPlayer) return true;
        if (!Input.GetKey(Plugin.WiringKey.Value)) return true;
        if (hold) return false; // swallow held Use while wiring so nothing repeats

        var machine = go != null ? go.GetComponentInParent<Machine>() : null;
        if (machine == null || !machine.ValidView) return true; // not a machine -> let vanilla run

        if (alt) {
            WiringTool.ClearOutputs(machine);
            __instance.Message(MessageHud.MessageType.Center, $"Cleared pipes from {machine.DisplayName}");
            return false;
        }

        var msg = WiringTool.Click(machine);
        if (!string.IsNullOrEmpty(msg)) __instance.Message(MessageHud.MessageType.Center, msg);
        return false; // handled: don't open the chest / add ore
    }
}
