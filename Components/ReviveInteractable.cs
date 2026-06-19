using UnityEngine;

namespace RevivalRevived.Components;

/// <summary>
/// Added to each ragdoll bone collider. Thin proxy that implements
/// Hoverable/Interactable and delegates to the linked player's
/// <see cref="Revivable"/>. Because a Revivable is attached on every client
/// while the player is downed (driven by the replicated ZDO flag), this works
/// the same on the owner and on remote clients.
/// </summary>
public class ReviveInteractable : MonoBehaviour, Hoverable, Interactable {
    private ZNetView? m_nview;

    private void Awake() {
        m_nview = GetComponentInParent<ZNetView>();
    }

    private Revivable? FindRevivable() {
        if (m_nview == null || !m_nview.IsValid()) return null;
        var player = DownedState.FindLinkedPlayer(m_nview.GetZDO());
        if (player == null || !DownedState.IsDowned(player)) return null;
        return player.GetComponent<Revivable>();
    }

    public string GetHoverText() {
        var revivable = FindRevivable();
        if (revivable == null) return "";

        var text = $"{revivable.PlayerName} (downed)\n";
        text += $"[<color=yellow><b>$KEY_Use</b></color>] Revive ({revivable.RemainingTime:F0}s)";

        var progress = revivable.Progress;
        if (progress > 0f) {
            text += $"\n<color=#7aa2f7>Reviving... {progress * 100f:F0}%</color>";
        }

        return Localization.instance.Localize(text);
    }

    public string GetHoverName() {
        var revivable = FindRevivable();
        return revivable?.PlayerName ?? "";
    }

    public bool Interact(Humanoid user, bool hold, bool alt) {
        if (!hold) return false;
        if (user is not Player reviver) return false;

        var revivable = FindRevivable();
        if (revivable == null || reviver == revivable.GetComponent<Player>()) return false;

        revivable.RequestRevive(reviver);
        return true;
    }

    public bool UseItem(Humanoid user, ItemDrop.ItemData item) {
        return false;
    }
}
