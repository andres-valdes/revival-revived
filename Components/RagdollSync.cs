using UnityEngine;

namespace RevivalRevived.Components;

/// <summary>
/// Makes a downed-player ragdoll's position owner-authoritative and smoothly
/// replicated to other clients.
///
/// Vanilla ragdolls are not position-synced: each client simulates its own copy
/// from the same initial velocity, so they drift apart. Here the owner (the
/// machine that spawned the ragdoll = the downed player's client) publishes the
/// average body position to the ZDO every physics tick. Remote clients stop
/// their independent simulation (kinematic bodies) and network-lerp the ragdoll
/// toward the authoritative position for smooth, convincing motion that stays
/// consistent across every client.
/// </summary>
public class RagdollSync : MonoBehaviour {
    private ZNetView? m_nview;
    private Ragdoll? m_ragdoll;
    private Rigidbody[] m_bodies = System.Array.Empty<Rigidbody>();
    private bool m_remoteFrozen;

    /// <summary>How quickly remote clients converge to the authoritative position.</summary>
    private const float LerpSpeed = 12f;

    private void Awake() {
        m_nview = GetComponent<ZNetView>();
        m_ragdoll = GetComponent<Ragdoll>();
        m_bodies = GetComponentsInChildren<Rigidbody>();
    }

    private void FixedUpdate() {
        if (m_nview == null || !m_nview.IsValid() || m_ragdoll == null) return;
        if (!m_nview.IsOwner()) return;

        // Authoritative: publish the simulated average body position.
        m_nview.GetZDO().Set(DownedState.s_ragdollPos, m_ragdoll.GetAverageBodyPosition());
    }

    private void Update() {
        if (m_nview == null || !m_nview.IsValid() || m_ragdoll == null) return;
        if (m_nview.IsOwner()) return; // owner simulates locally

        // Remote: stop independent physics so we don't fight the synced position.
        if (!m_remoteFrozen) {
            foreach (var rb in m_bodies) rb.isKinematic = true;
            m_remoteFrozen = true;
        }

        var synced = m_nview.GetZDO().GetVec3(DownedState.s_ragdollPos, transform.position);
        var currentAvg = m_ragdoll.GetAverageBodyPosition();
        // Translate the whole ragdoll so its average body position approaches the
        // authoritative one, lerped for smoothness.
        var desiredRoot = transform.position + (synced - currentAvg);
        transform.position = Vector3.Lerp(transform.position, desiredRoot, Time.deltaTime * LerpSpeed);
    }
}
