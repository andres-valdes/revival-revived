using System.Text;
using Automations.Domain;
using UnityEngine;

namespace Automations.Components;

/// <summary>
/// A machine's interaction surface (Hoverable + Interactable). It renders the
/// machine's live status in the hover panel and turns player input into one of two
/// actions:
///   - holding the wiring key + Use  -> connector click (pick source / lay pipe).
///   - Use alone                     -> cycle this machine's blueprint.
///   - alt-Use (block key)           -> clear this machine's outgoing pipes.
/// It never writes a ZDO itself; it forwards to <see cref="WiringTool"/> and
/// <see cref="Machine"/>, which own the authority rules.
/// </summary>
public class MachineInteract : MonoBehaviour, Hoverable, Interactable {
    private Machine m_machine = null!;

    private void Awake() {
        m_machine = GetComponent<Machine>();
    }

    private bool WiringHeld => Input.GetKey(Plugin.WiringKey.Value);

    public string GetHoverName() =>
        m_machine != null && m_machine.ValidView
            ? Localization.instance.Localize(m_machine.Def.DisplayName)
            : "";

    public string GetHoverText() {
        if (m_machine == null || !m_machine.ValidView) return "";
        var view = m_machine.View;
        var def = view.Def;

        var sb = new StringBuilder();
        sb.Append(def.DisplayName).Append('\n');
        sb.Append("Blueprint: <color=orange>").Append(m_machine.BlueprintLabel()).Append("</color>\n");

        var buffer = view.ReadBuffer();
        if (buffer.Count == 0) {
            sb.Append("<color=grey>empty</color>\n");
        } else {
            sb.Append("Holding: ");
            bool first = true;
            foreach (var kv in buffer) { if (!first) sb.Append(", "); sb.Append(kv.Value).Append(' ').Append(kv.Key); first = false; }
            sb.Append('\n');
        }
        sb.Append("Pipes out: ").Append(view.Outputs.Count).Append('\n');

        var pending = WiringTool.PendingSource;
        if (WiringHeld) {
            if (pending == null) sb.Append("[<color=yellow><b>$KEY_Use</b></color>] Start pipe here");
            else if (pending == m_machine) sb.Append("[<color=yellow><b>$KEY_Use</b></color>] (pipe start -- pick a target)");
            else sb.Append("[<color=yellow><b>$KEY_Use</b></color>] Connect pipe here");
        } else {
            if (def.BlueprintCount > 1) sb.Append("[<color=yellow><b>$KEY_Use</b></color>] Cycle blueprint   ");
            sb.Append("[<color=yellow><b>hold wiring key</b></color>] to pipe");
        }
        return Localization.instance.Localize(sb.ToString());
    }

    public bool Interact(Humanoid user, bool hold, bool alt) {
        if (hold) return false; // act on the press edge, not the hold
        if (m_machine == null || !m_machine.ValidView) return false;
        if (user is not Player player) return false;

        // alt-Use clears the machine's outgoing pipes.
        if (alt) {
            WiringTool.ClearOutputs(m_machine);
            player.Message(MessageHud.MessageType.Center, $"Cleared pipes from {m_machine.Def.DisplayName}");
            return true;
        }

        // Wiring key held -> connector click; otherwise cycle blueprint.
        if (WiringHeld) {
            var msg = WiringTool.Click(m_machine);
            if (!string.IsNullOrEmpty(msg)) player.Message(MessageHud.MessageType.Center, msg);
            return true;
        }

        if (m_machine.Def.BlueprintCount > 1) {
            m_machine.CycleBlueprint();
            player.Message(MessageHud.MessageType.Center, $"{m_machine.Def.DisplayName}: {m_machine.BlueprintLabel()}");
        } else {
            player.Message(MessageHud.MessageType.Center, $"{m_machine.Def.DisplayName}: {m_machine.BlueprintLabel()}");
        }
        return true;
    }

    public bool UseItem(Humanoid user, ItemDrop.ItemData item) => false;
}
