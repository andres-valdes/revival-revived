using RpcTyped;
using UnityEngine;
using ZdoTyped;

namespace RevivalRevived.Components;

/// <summary>
/// Added to the downed marker. Implements Hoverable/Interactable and owns the
/// revive channel <em>peer-authoritatively</em>: the reviving client accumulates
/// the hold timer locally (no owner round-trip, so the progress circle is
/// lag-free -- Valheim's trust model doesn't need host arbitration), publishes
/// progress onto the marker ZDO (claiming ownership) so the downed player and
/// bystanders see it too, pings the owner to pause the bleed-out window, and
/// sends <see cref="DownedRpcs.DoRevive"/> when the hold completes.
/// </summary>
public class ReviveInteractable : MonoBehaviour, Hoverable, Interactable {
    private ZNetView? m_nview;
    private float m_holdTimer;
    private float m_lastInteractTime = -999f;
    private float m_lastPingTime = -999f;
    private bool m_reviveSent;

    /// <summary>Player.Interact re-fires held interactions every 0.2s; anything under this counts as "still holding".</summary>
    private const float HoldGapTimeout = 0.4f;
    /// <summary>Cadence of pause-the-window pings to the downed player's owner.</summary>
    private const float PingInterval = 0.25f;

    private void Awake() {
        m_nview = GetComponentInParent<ZNetView>();
    }

    /// <summary>The downed player linked to this marker, or null.</summary>
    private Player? FindDownedPlayer() {
        if (!m_nview.TryGetZdo<DownedMarker.View>(out var view)) return null;
        var playerZdoId = view.Player;
        if (playerZdoId == ZDOID.None) return null;
        var playerZdo = ZDOMan.instance.GetZDO(playerZdoId);
        if (playerZdo == null) return null;
        var nview = ZNetScene.instance.FindInstance(playerZdo);
        var player = nview != null ? nview.GetComponent<Player>() : null;
        return player != null && player.IsDowned() ? player : null;
    }

    private void Update() {
        if (m_nview == null || !m_nview.IsValid()) return;

        bool channeling = Time.time - m_lastInteractTime < HoldGapTimeout;
        var player = channeling || m_holdTimer > 0f ? FindDownedPlayer() : null;

        if (channeling && player != null) {
            if (Plugin.RevivePressMode) {
                SendRevive(player);
                return;
            }

            m_holdTimer += Time.deltaTime;

            // Keep the owner's bleed-out window paused while we channel.
            if (Time.time - m_lastPingTime > PingInterval) {
                m_lastPingTime = Time.time;
                player.m_nview.GetRpcs<DownedRpcs>().Channel();
            }

            if (m_holdTimer >= Plugin.ReviveDuration) {
                SendRevive(player);
                return;
            }
        } else if (m_holdTimer > 0f) {
            m_holdTimer = Mathf.Max(0f, m_holdTimer - Time.deltaTime * 2f);
        } else {
            return; // idle: nothing to publish
        }

        PublishProgress(Mathf.Clamp01(m_holdTimer / Plugin.ReviveDuration));
    }

    /// <summary>
    /// Write our locally-authoritative progress to the marker ZDO so every other
    /// client's UI sees it (we claim the marker -- it's just a prop).
    /// </summary>
    private void PublishProgress(float progress) {
        if (m_nview == null || !m_nview.IsValid()) return;
        if (!m_nview.IsOwner()) m_nview.ClaimOwnership();
        var view = m_nview.GetZdo<DownedMarker.View>();
        view.ReviveProgress = progress;
    }

    private void SendRevive(Player player) {
        if (m_reviveSent) return;
        m_reviveSent = true;
        m_holdTimer = 0f;
        PublishProgress(0f);
        // Routed to the downed player's owner, which executes the revive.
        player.m_nview.GetRpcs<DownedRpcs>().DoRevive();
    }

    /// <summary>
    /// True when the linked player's session ZDO no longer exists at all --
    /// they disconnected while downed. (Distinct from the ZDO existing but the
    /// instance being unloaded by distance, which is not a disconnect.)
    /// </summary>
    private bool LinkedPlayerDisconnected() {
        if (!m_nview.TryGetZdo<DownedMarker.View>(out var view)) return false;
        var playerZdoId = view.Player;
        if (playerZdoId == ZDOID.None) return false;
        return ZDOMan.instance.GetZDO(playerZdoId) == null;
    }

    public string GetHoverText() {
        var player = FindDownedPlayer();
        if (player == null) {
            // The marker outlives a disconnecting owner (it is the evidence that
            // kills them on reconnect); tell the would-be reviver why there is
            // no revive prompt.
            if (LinkedPlayerDisconnected()) {
                var who = m_nview!.GetZdo<DownedMarker.View>().OwnerName is { Length: > 0 } dcName ? dcName : "Viking";
                return Localization.instance.Localize($"{who} (disconnected)");
            }
            return "";
        }

        var name = m_nview!.GetZdo<DownedMarker.View>().OwnerName is { Length: > 0 } n ? n : "Viking";
        var verb = Plugin.RevivePressMode ? "" : "Hold ";
        var text = $"{name} (downed)\n";
        text += $"[<color=yellow><b>{verb}$KEY_Use</b></color>] Revive ({player.GetDownedRemainingTime():F0}s)";
        return Localization.instance.Localize(text);
    }

    public string GetHoverName() {
        if (m_nview == null || !m_nview.IsValid()) return "";
        if (FindDownedPlayer() != null || LinkedPlayerDisconnected()) {
            var owner = m_nview.GetZdo<DownedMarker.View>().OwnerName;
            return string.IsNullOrEmpty(owner) ? "Viking" : owner;
        }
        return "";
    }

    public bool Interact(Humanoid user, bool hold, bool alt) {
        if (!hold && !Plugin.RevivePressMode) return false;
        if (user is not Player reviver) return false;

        var player = FindDownedPlayer();
        if (player == null || reviver == player) return false;

        m_lastInteractTime = Time.time; // the local Update drives the channel
        return true;
    }

    /// <summary>Test hook: equivalent to the local player holding interact this frame.</summary>
    public void SimulateHold() => m_lastInteractTime = Time.time;

    public bool UseItem(Humanoid user, ItemDrop.ItemData item) {
        return false;
    }
}
