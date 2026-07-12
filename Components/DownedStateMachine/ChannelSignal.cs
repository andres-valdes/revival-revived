using UnityEngine;

namespace ReviveAllies.Components;

/// <summary>
/// The "a reviver is channeling me" signal as its own Player component. The reviver
/// sends a single begin edge when they start and a single end edge when they let go
/// (<see cref="DownedKeys.RpcChannel"/> carrying a bool and their player ZDOID) -- no
/// per-frame heartbeat.
/// The owner records it here; the downed machine's Waiting/Reviving states transition
/// on <see cref="IsChanneling"/>.
///
/// The one thing a heartbeat gave us for free was liveness, so we fold it into the
/// getter: if the reviver disconnects before their end edge arrives, their session
/// player ZDO disappears and the channel ends (otherwise the anchor would keep
/// accumulating and silently complete the revive for a reviver who left). A routed RPC
/// sender cannot be checked with ZNet.IsConnected: clients are direct peers of the
/// server, not of each other.
/// </summary>
public class ChannelSignal : MonoBehaviour {
    private ZNetView m_nview = null!;
    private bool m_channeling;
    private long m_channeler;
    private ZDOID m_channelerZdo = ZDOID.None;

    public bool IsChanneling =>
        m_channeling && m_channelerZdo != ZDOID.None &&
        ZDOMan.instance != null && ZDOMan.instance.GetZDO(m_channelerZdo) != null;

    public long LastChanneler => m_channeler;

    private void Awake() {
        m_nview = GetComponent<ZNetView>();

        // Reviver -> owner: "I started / stopped channeling a revive." Registered on
        // every client; only the owner records it. Drives the reviving/waiting
        // transition (and thus the anchor writes).
        m_nview.Register<bool, ZDOID>(DownedKeys.RpcChannel, (long sender, bool channeling, ZDOID channelerZdo) => {
            if (!m_nview.IsOwner()) return;

            // An end edge delayed from an earlier reviver must not cancel the current
            // reviver's channel.
            if (!channeling && channelerZdo != m_channelerZdo) return;

            m_channeler = sender;
            m_channelerZdo = channelerZdo;
            m_channeling = channeling;
        });
    }
}
