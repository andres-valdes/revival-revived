using System;
using UnityEngine;

namespace ReviveAllies.Components;

/// <summary>
/// The revive channel as a decaying timer, baked into the marker. It never ticks
/// over the network: the marker owner writes a small anchor to the ZDO
/// (<see cref="ReviveChannelDecayingProgressView"/>) only when a channel begins or ends, and every
/// client -- reviver and owner alike -- computes the live fraction locally from
/// that anchor and the synced world clock.
///
/// The owner, purely from local computation, revives
/// the linked player when the fill completes and pauses the bleed-out window by
/// the time spent channeling. <see cref="Finished"/> fires on every client when
/// the fill empties (decays to zero, or the marker is destroyed), so the injected
/// <see cref="ProgressUI"/> despawns.
/// </summary>
public class ReviveChannelDecayingProgress : MonoBehaviour, IDecayingProgress {
    /// <summary>Channel seconds lost per real second while not being channeled.</summary>
    private const float DecayRate = 2f;

    private ZNetView m_nview = null!;
    private long m_channeler;
    private float m_channelStart = -1f;   // owner-local: when the current channel began (for the window pause); <0 = none
    private bool m_active;                 // Finished edge

    public event Action? Finished;

    private void Awake() => m_nview = GetComponent<ZNetView>();

    private bool Owner => m_nview != null && m_nview.IsValid() && m_nview.IsOwner();
    private float Now => (float)ZNet.instance.GetTimeSeconds();
    private ReviveChannelDecayingProgressView View => new(m_nview);

    /// <summary>Live channel seconds, computed locally from the replicated anchor.</summary>
    private float Seconds() {
        if (m_nview == null || !m_nview.IsValid()) return 0f;
        var v = View;
        float since = Now - v.AnchorTime;
        return v.Channeling ? v.AnchorSeconds + since
                            : Mathf.Max(0f, v.AnchorSeconds - since * DecayRate);
    }

    public float Fraction => Mathf.Clamp01(Seconds() / Plugin.ReviveDuration);

    /// <summary>The owner has registered this channel (the anchor says "channeling"); the reviver waits for this before showing progress locally.</summary>
    public bool Acked => m_nview != null && m_nview.IsValid() && View.Channeling;

    /// <summary>Owner: a channel started (RevivingState entered). Anchor the timeline; no per-frame writes follow.</summary>
    public void Begin(long channeler) {
        if (!Owner) return;
        m_channeler = channeler;
        m_channelStart = Now;
        var v = View;
        v.AnchorSeconds = Seconds();   // fold any decay accrued while idle
        v.AnchorTime = Now;
        v.Channeling = true;
    }

    /// <summary>Owner: the channel stopped (RevivingState exited). Freeze the accumulator and pause the window by the channeled time.</summary>
    public void End() {
        if (!Owner) return;
        var v = View;
        v.AnchorSeconds = Seconds();
        v.AnchorTime = Now;
        v.Channeling = false;
        if (m_channelStart >= 0f) PauseWindow(Now - m_channelStart);
        m_channelStart = -1f;
    }

    private void Update() {
        // Completion is a local computation on the owner -- still no network tick.
        if (Owner) {
            var v = View;
            if (v.Channeling && (Plugin.RevivePressMode || Seconds() >= Plugin.ReviveDuration)) {
                v.Channeling = false;
                v.AnchorSeconds = 0f;
                v.AnchorTime = Now;
                LinkedPlayer()?.ReviveFromDowned(m_channeler);   // crumbles the marker -> OnDestroy -> Finished
                return;
            }
        }

        // Finished edge (every client): the fill has emptied.
        if (Fraction > 0.01f) m_active = true;
        else if (m_active) { m_active = false; Finished?.Invoke(); }
    }

    private void OnDestroy() {
        if (m_active) { m_active = false; Finished?.Invoke(); }
    }

    /// <summary>Shift the linked player's bleed-out clock forward by time spent channeling (owner writes once, on stop).</summary>
    private void PauseWindow(float seconds) {
        if (seconds <= 0f) return;
        var player = LinkedPlayer();
        if (player == null || !player.m_nview.IsValid() || !player.m_nview.IsOwner()) return;
        var s = new DownedStateMachineView(player.m_nview);
        s.DownedTime += seconds;
    }

    private Player? LinkedPlayer() {
        if (m_nview == null || !m_nview.IsValid()) return null;
        var id = new DownedMarkerView(m_nview).LinkedPlayer;
        if (id == ZDOID.None) return null;
        var zdo = ZDOMan.instance.GetZDO(id);
        var nv = zdo != null ? ZNetScene.instance.FindInstance(zdo) : null;
        return nv != null ? nv.GetComponent<Player>() : null;
    }
}
