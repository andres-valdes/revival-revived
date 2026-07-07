using System.Text;
using UnityEngine;

namespace Automations.Components;

/// <summary>
/// The assembler's interaction surface. Pressing Use cycles its blueprint; the
/// hover panel shows the current recipe and buffered items. When the wiring key is
/// held, the connector patch intercepts Use first (to lay pipes), so the two never
/// conflict.
/// </summary>
public class AssemblerInteract : MonoBehaviour, Hoverable, Interactable {
    private Assembler m_asm = null!;

    private void Awake() => m_asm = GetComponent<Assembler>();

    public string GetHoverName() => Localization.instance.Localize("Blueprint Assembler");

    public string GetHoverText() {
        if (m_asm == null || !m_asm.ValidView) return "";
        var v = m_asm.View;
        var sb = new StringBuilder();
        sb.Append("Blueprint Assembler\n");
        sb.Append("Blueprint: <color=orange>").Append(v.Recipe.Describe()).Append("</color>\n");
        var buf = v.ReadBuffer();
        if (buf.Count == 0) sb.Append("<color=grey>empty</color>\n");
        else {
            sb.Append("Holding: ");
            bool first = true;
            foreach (var kv in buf) { if (!first) sb.Append(", "); sb.Append(kv.Value).Append(' ').Append(kv.Key); first = false; }
            sb.Append('\n');
        }
        sb.Append("[<color=yellow><b>$KEY_Use</b></color>] Cycle blueprint   [<color=yellow><b>hold wiring key</b></color>] to pipe");
        return Localization.instance.Localize(sb.ToString());
    }

    public bool Interact(Humanoid user, bool hold, bool alt) {
        if (hold) return false;
        if (m_asm == null || !m_asm.ValidView || user is not Player player) return false;
        m_asm.CycleBlueprint();
        player.Message(MessageHud.MessageType.Center, $"Blueprint: {m_asm.View.Recipe.Describe()}");
        return true;
    }

    public bool UseItem(Humanoid user, ItemDrop.ItemData item) => false;
}
