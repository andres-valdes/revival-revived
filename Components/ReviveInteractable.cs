using UnityEngine;

namespace RevivalRevived.Components;

/// <summary>
/// Added to each ragdoll bone collider. Thin proxy that implements
/// Hoverable/Interactable and delegates to the linked player's Revivable.
/// </summary>
public class ReviveInteractable : MonoBehaviour, Hoverable, Interactable {
    private ZNetView? m_nview;

    private void Awake() {
        m_nview = GetComponentInParent<ZNetView>();
    }

    private Revivable? FindRevivable() {
        if (m_nview == null || !m_nview.IsValid()) return null;
        var player = DownedState.FindLinkedPlayer(m_nview.GetZDO());
        return player != null ? player.GetComponent<Revivable>() : null;
    }

    public string GetHoverText() {
        var revivable = FindRevivable();
        if (revivable == null) return "";

        var remaining = revivable.GetRemainingTime();
        var text = $"{revivable.PlayerName} (downed)\n";
        text += $"[<color=yellow><b>$KEY_Use</b></color>] Revive ({remaining:F0}s)";

        if (revivable.HoldTimer > 0f) {
            var progress = Mathf.Clamp01(revivable.HoldTimer / DownedState.ReviveDuration);
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

        var reviver = user as Player;
        if (reviver == null) return false;

        var revivable = FindRevivable();
        return revivable != null && revivable.TryRevive(reviver);
    }

    public bool UseItem(Humanoid user, ItemDrop.ItemData item) {
        return false;
    }
}
