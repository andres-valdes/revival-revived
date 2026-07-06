using UnityEngine;

namespace ReviveAllies.Components;

/// <summary>
/// The marker's interaction surface: Hoverable/Interactable only. It shows the
/// revive hover prompt and, on interact, forwards the intent to the reviving
/// player's <see cref="ReviveRequest"/> -- it is NOT part of any state machine
/// and never writes a ZDO. The reviver machine (on the Player) owns the channeling
/// behaviour.
/// </summary>
public class Revivable : MonoBehaviour, Hoverable, Interactable {
    private ZNetView? m_nview;

    private void Awake() {
        m_nview = GetComponentInParent<ZNetView>();
    }

    /// <summary>The character ZDOID this marker is linked to, or None.</summary>
    private ZDOID LinkedPlayerZdoId() =>
        m_nview != null && m_nview.IsValid()
            ? new DownedMarkerView(m_nview).LinkedPlayer
            : ZDOID.None;

    /// <summary>The downed player linked to this marker, or null.</summary>
    private Player? FindDownedPlayer() {
        var playerZdoId = LinkedPlayerZdoId();
        if (playerZdoId == ZDOID.None) return null;
        var playerZdo = ZDOMan.instance.GetZDO(playerZdoId);
        if (playerZdo == null) return null;
        var nview = ZNetScene.instance.FindInstance(playerZdo);
        var player = nview != null ? nview.GetComponent<Player>() : null;
        return player != null && player.IsDowned() ? player : null;
    }

    /// <summary>
    /// True when the linked player's session ZDO no longer exists at all -- they
    /// disconnected while downed. (Distinct from the ZDO existing but the instance
    /// being unloaded by distance, which is not a disconnect.)
    /// </summary>
    private bool LinkedPlayerDisconnected() {
        var playerZdoId = LinkedPlayerZdoId();
        if (playerZdoId == ZDOID.None) return false;
        return ZDOMan.instance.GetZDO(playerZdoId) == null;
    }

    /// <summary>The downed player's name from the marker ZDO, or "Viking".</summary>
    private string OwnerName() {
        if (m_nview == null || !m_nview.IsValid()) return "Viking";
        var name = new DownedMarkerView(m_nview).OwnerName;
        return string.IsNullOrEmpty(name) ? "Viking" : name;
    }

    public string GetHoverText() {
        var player = FindDownedPlayer();
        if (player == null) {
            // The marker outlives a disconnecting owner (it is the evidence that
            // kills them on reconnect); tell the would-be reviver why there is
            // no revive prompt.
            if (LinkedPlayerDisconnected()) {
                return Localization.instance.Localize($"{OwnerName()} (disconnected)");
            }
            return "";
        }

        var name = OwnerName();
        var verb = Plugin.RevivePressMode ? "" : "Hold ";
        var text = $"{name} (downed)\n";
        text += $"[<color=yellow><b>{verb}$KEY_Use</b></color>] Revive ({player.GetDownedRemainingTime():F0}s)";
        return Localization.instance.Localize(text);
    }

    public string GetHoverName() {
        if (m_nview == null || !m_nview.IsValid()) return "";
        if (FindDownedPlayer() != null || LinkedPlayerDisconnected()) {
            return OwnerName();
        }
        return "";
    }

    public bool Interact(Humanoid user, bool hold, bool alt) {
        if (!hold && !Plugin.RevivePressMode) return false;
        if (user is not Player reviver) return false;

        var player = FindDownedPlayer();
        if (player == null || reviver == player) return false;

        reviver.GetComponent<ReviveRequest>()?.Request(player); // the reviver machine channels
        return true;
    }

    /// <summary>
    /// Test hook: one frame of channel input from the local player as would-be
    /// reviver. Self allowed here (single-process tests revive the local downed
    /// player); the real Interact path forbids reviving yourself.
    /// </summary>
    public void SimulateHold() {
        var player = FindDownedPlayer();
        var me = Player.m_localPlayer;
        if (player != null && me != null) {
            me.GetComponent<ReviveRequest>()?.Request(player);
        }
    }

    public bool UseItem(Humanoid user, ItemDrop.ItemData item) {
        return false;
    }
}
