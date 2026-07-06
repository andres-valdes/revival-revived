using UnityEngine;

namespace ReviveAllies.Components;

/// <summary>
/// The reviver's channeling context as its own Player component: who the local
/// player is reviving, how recently they interacted, and the green circle. The
/// marker's <see cref="Revivable"/> calls <see cref="Request"/>; the reviver
/// machine's states read it off the Player.
///
/// The reviver only relays two edges to the victim's owner -- <see cref="SendBegin"/>
/// when they start and <see cref="SendEnd"/> when they let go -- and only shows the
/// circle once the owner has ACKed by registering the channel (the anchor comes back,
/// <see cref="ReviveChannelDecayingProgress.Acked"/>). So the reviver never displays a
/// revive the victim hasn't actually started.
/// </summary>
public class ReviveRequest : MonoBehaviour {
    /// <summary>How long after the last interact frame we still count as channeling (vanilla hold-interact has no release event).</summary>
    private const float RequestTimeout = 0.25f;

    private Player? m_target;
    private float m_lastRequest = -999f;
    private ProgressUI? m_ui;

    /// <summary>The marker's interact reports the local player wants to revive <paramref name="downed"/>.</summary>
    public void Request(Player downed) {
        m_target = downed;
        m_lastRequest = Time.time;
    }

    /// <summary>The local player interacted on a marker recently.</summary>
    public bool WantsToChannel => Time.time - m_lastRequest < RequestTimeout;

    /// <summary>The downed player currently being revived (still downed), or null.</summary>
    public Player? Target => m_target != null && m_target.IsDowned() ? m_target : null;

    /// <summary>Tell the victim's owner we started channeling (one edge).</summary>
    public void SendBegin() {
        if (m_target != null && m_target.m_nview.IsValid()) m_target.m_nview.InvokeRPC(DownedKeys.RpcChannel, true);
    }

    /// <summary>Tell the victim's owner we stopped channeling (one edge).</summary>
    public void SendEnd() {
        if (m_target != null && m_target.m_nview.IsValid()) m_target.m_nview.InvokeRPC(DownedKeys.RpcChannel, false);
    }

    /// <summary>
    /// Spawn the green circle once the owner has ACKed the channel (the marker anchor
    /// came back). Until then we've sent begin but show nothing -- no reviving locally
    /// before the victim confirms.
    /// </summary>
    public void ShowUI() {
        if (m_ui != null) return;
        var timer = Target?.FindDownedMarker()?.GetComponent<ReviveChannelDecayingProgress>();
        if (timer != null && timer.Acked) m_ui = ProgressUI.Create(timer, DownedMarker.ReviveGreen, isGiveUp: false);
    }

    /// <summary>Tear the circle down when the local player stops channeling (they let go / the target was revived).</summary>
    public void CloseUI() {
        m_ui?.Close();
        m_ui = null;
    }
}
