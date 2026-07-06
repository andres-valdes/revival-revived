using UnityEngine;

namespace ReviveAllies.Components;

/// <summary>
/// The "a reviver is channeling me" signal as its own Player component. The reviver
/// sends a single begin edge when they start and a single end edge when they let go
/// (<see cref="DownedKeys.RpcChannel"/> carrying a bool) -- no per-frame heartbeat.
/// The owner records it here; the downed machine's Waiting/Reviving states transition
/// on <see cref="IsChanneling"/>.
///
/// The one thing a heartbeat gave us for free was liveness, so we fold it into the
/// getter: if the reviver disconnects before their end edge arrives, they drop out of
/// <see cref="ZNet.IsConnected"/> and the channel ends (otherwise the anchor would keep
/// accumulating and silently complete the revive for a reviver who left). Self counts
/// as connected, so single-player / self-revive works.
/// </summary>
public class ChannelSignal : MonoBehaviour {
    private ZNetView m_nview = null!;
    private bool m_channeling;
    private long m_channeler;

    public bool IsChanneling =>
        m_channeling && ZNet.instance != null && ZNet.instance.IsConnected(m_channeler);

    public long LastChanneler => m_channeler;

    private void Awake() {
        m_nview = GetComponent<ZNetView>();

        // Reviver -> owner: "I started / stopped channeling a revive." Registered on
        // every client; only the owner records it. Drives the reviving/waiting
        // transition (and thus the anchor writes).
        m_nview.Register<bool>(DownedKeys.RpcChannel, (long sender, bool channeling) => {
            if (!m_nview.IsOwner()) return;
            m_channeler = sender;
            m_channeling = channeling;
        });
    }
}
