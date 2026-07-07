using System.Collections.Generic;
using Automations.Domain;
using UnityEngine;

namespace Automations.Components;

/// <summary>
/// The pipe node. Attached (by <see cref="Patches.MachineAttachPatch"/>) to every
/// vanilla piece that can be part of a factory -- a chest, a smelter, a kiln -- it
/// adds a directional-pipe graph on top without touching how that piece behaves.
/// The piece keeps all its own logic and visuals; this component only, on the
/// owning peer, moves items from its <see cref="IMachinePort"/> down each pipe into
/// the downstream machine's port.
///
/// Because the port bridges to the vanilla component's REAL storage, a piped
/// smelter still lights its fire and smokes as ore and fuel arrive, and a piped
/// chest is still a chest you can open.
/// </summary>
public class Machine : MonoBehaviour {
    /// <summary>Every live machine on this peer (for wiring, rendering, discovery).</summary>
    public static readonly List<Machine> All = new();

    private ZNetView m_nview = null!;
    private IMachinePort? m_port;
    private bool m_portResolved;
    private float m_nextTick;

    private void Awake() {
        m_nview = GetComponent<ZNetView>();
        if (m_nview == null) return;
        All.Add(this);
        m_nextTick = Time.time + Random.Range(0f, Plugin.TickInterval);
    }

    private void OnDestroy() => All.Remove(this);

    // --------------------------------------------------------------- convenience
    public bool ValidView => m_nview != null && m_nview.IsValid();
    public ZNetView Nview => m_nview;
    public MachineView View => new(m_nview);
    public ZDOID ZdoId => m_nview.GetZDO().m_uid;
    public bool IsOwner => m_nview != null && m_nview.IsOwner();
    public void Claim() { if (m_nview != null && m_nview.IsValid() && !m_nview.IsOwner()) m_nview.ClaimOwnership(); }

    /// <summary>A short label for what kind of machine this is.</summary>
    public string DisplayName {
        get {
            if (GetComponent<Assembler>() != null) return "Blueprint Assembler";
            var c = GetComponent<Container>();
            if (c != null) return string.IsNullOrEmpty(c.m_name) ? "Chest" : c.m_name;
            var s = GetComponent<Smelter>();
            if (s != null) return string.IsNullOrEmpty(s.m_name) ? "Smelter" : s.m_name;
            return "Machine";
        }
    }

    /// <summary>The bridge to this piece's real storage, resolved once from its components.</summary>
    public IMachinePort? Port {
        get {
            if (m_portResolved) return m_port;
            m_portResolved = true;
            if (GetComponent<Assembler>() != null) { m_port = new AssemblerPort(m_nview); return m_port; }
            var container = GetComponent<Container>();
            if (container != null) { m_port = new ContainerPort(container); return m_port; }
            var smelter = GetComponent<Smelter>();
            if (smelter != null) { m_port = new SmelterPort(smelter, m_nview); return m_port; }
            return m_port; // null: not a machine we know how to bridge
        }
    }

    // --------------------------------------------------------------------- ticking
    private void Update() {
        if (!ValidView || !m_nview.IsOwner()) return;
        if (Time.time < m_nextTick) return;
        m_nextTick = Time.time + Plugin.TickInterval;
        Tick();
    }

    private void Tick() {
        var myPort = Port;
        if (myPort == null) return;
        var outs = View.Outputs;
        if (outs.Count == 0) return;

        foreach (var targetId in outs) {
            var go = ZNetScene.instance != null ? ZNetScene.instance.FindInstance(targetId) : null;
            if (go == null) continue; // downstream not loaded here this tick
            var tm = go.GetComponent<Machine>();
            var tport = tm != null ? tm.Port : null;
            if (tport == null) continue;

            int budget = Plugin.TransferPerTick;
            foreach (var kv in new List<KeyValuePair<string, int>>(myPort.Outputs())) {
                if (budget <= 0) break;
                var item = kv.Key;
                if (!tport.Accepts(item)) continue;
                int move = Mathf.Min(budget, Mathf.Min(kv.Value, tport.InputFree(item)));
                if (move <= 0) continue;

                int got = myPort.Withdraw(item, move);
                if (got <= 0) continue;
                int accepted = tport.Deliver(item, got);
                if (accepted < got) {
                    // Downstream took fewer than its declared free space (rare). Don't
                    // lose the remainder -- push it back into our own output side.
                    myPort.Deliver(item, got - accepted);
                }
                budget -= accepted;
            }
        }
    }

    // --------------------------------------------------------- discovery / tests
    /// <summary>Count of an item currently available to ship (safe on any peer).</summary>
    public int Available(string item) {
        var port = Port;
        if (port == null) return 0;
        int total = 0;
        foreach (var kv in port.Outputs()) if (kv.Key == item) total += kv.Value;
        return total;
    }

    public static Machine? FindByTag(string tag) {
        foreach (var m in All) if (m != null && m.ValidView && m.View.Tag == tag) return m;
        return null;
    }

    /// <summary>
    /// Spawn a vanilla machine piece (chest / smelter / kiln) at a grounded position
    /// and tag it. Used by the E2E harness; normal play just builds these with the
    /// hammer and the attach patch makes them machines automatically.
    /// </summary>
    public static Machine? SpawnVanilla(string prefabName, Vector3 pos, Quaternion rot, string tag = "") {
        var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefabName) : null;
        if (prefab == null) {
            Plugin.Logger.LogError($"Machine.SpawnVanilla: prefab '{prefabName}' not found");
            return null;
        }
        if (ZoneSystem.instance != null) {
            pos = new Vector3(pos.x, ZoneSystem.instance.GetGroundHeight(pos), pos.z);
        }
        var go = Object.Instantiate(prefab, pos, rot);
        var nview = go.GetComponent<ZNetView>();
        if (nview == null || !nview.IsValid()) {
            Plugin.Logger.LogError($"Machine.SpawnVanilla: '{prefabName}' has no valid ZNetView");
            return null;
        }
        var m = go.GetComponent<Machine>() ?? go.AddComponent<Machine>();
        if (!string.IsNullOrEmpty(tag)) { var v = new MachineView(nview); v.Tag = tag; }
        return m;
    }
}
