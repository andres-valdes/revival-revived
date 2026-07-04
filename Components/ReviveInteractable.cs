using UnityEngine;

namespace RevivalRevived.Components;

/// <summary>
/// Added to the downed marker. Implements Hoverable/Interactable and forwards
/// the reviver's intent to the downed player's owner.
///
/// The revive is <em>owner-authoritative</em>: while a reviver holds (or presses
/// in press mode) the interact key on the marker, we send a <see cref="DownedKeys.RpcChannel"/>
/// ping routed to the downed player's owner. The owner accumulates the progress
/// on its own ZDO, publishes it (so every client's UI sees it replicate in), and
/// revives itself at completion. This component never writes any ZDO -- so it
/// cannot claim the marker, fight its owner, or leave stale progress behind.
/// The cost is that the reviver's progress circle lags by one round-trip, which
/// is correct.
/// </summary>
public class ReviveInteractable : MonoBehaviour, Hoverable, Interactable {
    private ZNetView? m_nview;
    private float m_lastPingTime = -999f;

    /// <summary>Cadence of channel pings to the downed player's owner (bounds RPC rate regardless of caller frequency).</summary>
    private const float PingInterval = 0.2f;

    private void Awake() {
        m_nview = GetComponentInParent<ZNetView>();
    }

    /// <summary>The character ZDOID this marker is linked to, or None.</summary>
    private ZDOID LinkedPlayerZdoId() =>
        m_nview != null && m_nview.IsValid()
            ? m_nview.GetZDO().GetZDOID(DownedKeys.MarkerPlayer)
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
    /// Tell the downed player's owner we are channeling (throttled). The owner
    /// does everything else.
    /// </summary>
    private void SendChannelPing(Player downed) {
        if (Time.time - m_lastPingTime < PingInterval) return;
        m_lastPingTime = Time.time;
        downed.m_nview.InvokeRPC(DownedKeys.RpcChannel);
    }

    /// <summary>
    /// True when the linked player's session ZDO no longer exists at all --
    /// they disconnected while downed. (Distinct from the ZDO existing but the
    /// instance being unloaded by distance, which is not a disconnect.)
    /// </summary>
    private bool LinkedPlayerDisconnected() {
        var playerZdoId = LinkedPlayerZdoId();
        if (playerZdoId == ZDOID.None) return false;
        return ZDOMan.instance.GetZDO(playerZdoId) == null;
    }

    /// <summary>The downed player's name from the marker ZDO, or "Viking".</summary>
    private string OwnerName() {
        if (m_nview == null || !m_nview.IsValid()) return "Viking";
        var name = m_nview.GetZDO().GetString(DownedKeys.OwnerName);
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

        SendChannelPing(player);
        return true;
    }

    /// <summary>Test hook: one frame of channel input from a would-be reviver.</summary>
    public void SimulateHold() {
        var player = FindDownedPlayer();
        if (player != null) SendChannelPing(player);
    }

    public bool UseItem(Humanoid user, ItemDrop.ItemData item) {
        return false;
    }
}
