using Automations.Components;
using HarmonyLib;

namespace Automations.Patches;

/// <summary>
/// Make vanilla pieces into machines: attach a <see cref="Machine"/> to every
/// Container (chests) and every Smelter (smelter, charcoal kiln, blast furnace...)
/// as it awakes. The piece is otherwise untouched -- it keeps all its own behaviour
/// and visuals and simply gains a pipe graph. Only blueprint machines (the
/// assembler) need a bespoke prefab; everything else is a real vanilla piece.
/// </summary>
[HarmonyPatch(typeof(Container), "Awake")]
static class ContainerAwakeAttachPatch {
    static void Postfix(Container __instance) {
        if (__instance.GetComponent<Machine>() == null) __instance.gameObject.AddComponent<Machine>();
    }
}

[HarmonyPatch(typeof(Smelter), "Awake")]
static class SmelterAwakeAttachPatch {
    static void Postfix(Smelter __instance) {
        if (__instance.GetComponent<Machine>() == null) __instance.gameObject.AddComponent<Machine>();
    }
}

/// <summary>
/// Redirect a smelter's output into the pipe network. Vanilla
/// <see cref="Smelter.Spawn"/> drops the finished bar on the ground; when the
/// smelter is a machine with an outgoing pipe and room in its capture buffer, we
/// take the bar into that buffer instead (and skip the drop) so a pipe can ship it.
/// With no pipe, or a full buffer, it drops as normal -- an un-piped smelter behaves
/// exactly like vanilla.
/// </summary>
[HarmonyPatch(typeof(Smelter), "Spawn")]
static class SmelterSpawnCapturePatch {
    static bool Prefix(Smelter __instance, string ore, int stack) {
        var machine = __instance.GetComponent<Machine>();
        if (machine == null || !machine.ValidView || !machine.IsOwner) return true;
        if (machine.View.Outputs.Count == 0) return true;          // not piped -> vanilla drop
        if (machine.Port is not SmelterPort port) return true;

        var conv = __instance.GetItemConversion(ore);
        var produced = conv != null && conv.m_to != null ? conv.m_to.gameObject.name : null;
        if (produced == null) return true;

        return !port.CaptureOutput(produced, stack); // captured -> skip drop; full -> drop
    }
}
