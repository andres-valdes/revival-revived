using System.Collections.Generic;
using Automations.Domain;
using UnityEngine;

namespace Automations.Components;

/// <summary>
/// The runtime behaviour of a machine. Deliberately thin: all state is on the ZDO
/// (<see cref="MachineView"/>); this class only drives the per-tick simulation and
/// only on the peer that OWNS the ZDO (Valheim's single-writer rule). Every tick a
/// machine, in order:
///   1. PRODUCE  -- a Stockpile mints its configured raw item.
///   2. PROCESS  -- a processor consumes its blueprint's inputs into its outputs.
///   3. TRANSFER -- ship buffered items down each outgoing pipe to a machine that
///                  will accept them, respecting the downstream's capacity.
///
/// Cross-machine transfer is the interesting bit: a machine can only write its own
/// ZDO, so it decrements its own buffer locally and sends an <see cref="RpcAccept"/>
/// to the DOWNSTREAM owner, who adds to theirs. That routes correctly even when the
/// two machines are owned by different clients -- which is exactly what makes a
/// factory work across the network.
/// </summary>
public class Machine : MonoBehaviour {
    public const string RpcAccept = MachineCatalog.Prefix + "Accept";

    /// <summary>Every live machine instance on this peer (for wiring/rendering/discovery).</summary>
    public static readonly List<Machine> All = new();

    private ZNetView m_nview = null!;
    private float m_nextTick;

    private void Awake() {
        m_nview = GetComponent<ZNetView>();
        if (m_nview == null) return;
        // Register/join unconditionally: RPC registration is ZDO-independent, and
        // the ZDO may not be valid yet at Awake (component-order race with
        // ZNetView.Awake). Every method that touches the ZDO guards IsValid, and a
        // placement ghost (no ZDO) simply stays ValidView=false and is ignored.
        m_nview.Register<string, int>(RpcAccept, RPC_Accept);
        All.Add(this);
        // Stagger first ticks so a freshly-loaded factory doesn't process in lockstep.
        m_nextTick = Time.time + Random.Range(0f, Plugin.TickInterval);
    }

    private void OnDestroy() => All.Remove(this);

    // --------------------------------------------------------------- convenience
    public bool ValidView => m_nview != null && m_nview.IsValid();
    public MachineView View => new(m_nview);
    public MachineDef Def => View.Def;
    public MachineKind Kind => View.Kind;
    public ZDOID ZdoId => m_nview.GetZDO().m_uid;
    public bool IsOwner => m_nview != null && m_nview.IsOwner();

    /// <summary>Take ownership so this peer can author the machine's ZDO.</summary>
    public void Claim() { if (m_nview != null && m_nview.IsValid() && !m_nview.IsOwner()) m_nview.ClaimOwnership(); }

    // --------------------------------------------------------------------- ticking
    private void Update() {
        if (!ValidView || !m_nview.IsOwner()) return;
        if (Time.time < m_nextTick) return;
        m_nextTick = Time.time + Plugin.TickInterval;
        Tick();
    }

    private void Tick() {
        var view = View;
        var def = view.Def;
        Produce(view, def);
        Process(view, def);
        Transfer(view, def);
    }

    private static void Produce(MachineView view, MachineDef def) {
        if (def.Kind != MachineKind.Stockpile) return;
        var raw = def.RawItemAt(view.Blueprint);
        if (raw != null) view.Add(raw, Plugin.ProduceRate, def.Capacity);
    }

    private static void Process(MachineView view, MachineDef def) {
        var recipe = def.RecipeAt(view.Blueprint);
        if (recipe == null) return;

        for (int batch = 0; batch < Plugin.ProcessPerTick; batch++) {
            // Inputs present?
            foreach (var need in recipe.Inputs) {
                if (view.Count(need.Key) < need.Value) return;
            }
            // Output room?
            foreach (var made in recipe.Outputs) {
                if (view.FreeSpace(made.Key, def.Capacity) < made.Value) return;
            }
            // Run the reaction.
            foreach (var need in recipe.Inputs) view.Remove(need.Key, need.Value);
            foreach (var made in recipe.Outputs) view.Add(made.Key, made.Value, def.Capacity);
        }
    }

    private void Transfer(MachineView view, MachineDef def) {
        var outputs = view.Outputs;
        if (outputs.Count == 0) return;

        foreach (var targetId in outputs) {
            var go = ZNetScene.instance != null ? ZNetScene.instance.FindInstance(targetId) : null;
            if (go == null) continue; // downstream not loaded on this peer this tick
            var tnview = go.GetComponent<ZNetView>();
            if (tnview == null || !tnview.IsValid()) continue;
            var tview = new MachineView(tnview);
            var tdef = tview.Def;

            int movedToTarget = 0;
            // Snapshot keys: we mutate our buffer inside the loop.
            foreach (var item in new List<string>(view.ReadBuffer().Keys)) {
                if (movedToTarget >= Plugin.TransferPerTick) break;
                if (!tdef.Accepts(item)) continue;
                int free = tview.FreeSpace(item, tdef.Capacity);
                int have = view.Count(item);
                int move = Mathf.Min(Plugin.TransferPerTick - movedToTarget, Mathf.Min(free, have));
                if (move <= 0) continue;

                // Decrement locally (we own our ZDO), then hand off to the
                // downstream owner via a routed RPC.
                view.Remove(item, move);
                tnview.InvokeRPC(Machine.RpcAccept, item, move);
                movedToTarget += move;
            }
        }
    }

    /// <summary>
    /// Downstream half of a transfer: the sender pre-checked our capacity, so we
    /// add (still clamped defensively). Only the owner mutates the ZDO.
    /// </summary>
    private void RPC_Accept(long sender, string item, int qty) {
        if (!ValidView || !m_nview.IsOwner()) return;
        var view = View;
        var def = view.Def;
        if (!def.Accepts(item)) return;
        view.Add(item, qty, def.Capacity);
    }

    // --------------------------------------------------------- blueprint / wiring
    /// <summary>Advance the selected blueprint (owner-authored; claims first).</summary>
    public void CycleBlueprint() {
        if (!ValidView) return;
        Claim();
        var view = View;
        int count = view.Def.BlueprintCount;
        if (count <= 1) return;
        view.Blueprint = (view.Blueprint + 1) % count;
    }

    /// <summary>Set the selected blueprint directly (owner-authored; claims first).</summary>
    public void SetBlueprint(int index) {
        if (!ValidView) return;
        Claim();
        var view = View;
        int count = view.Def.BlueprintCount;
        if (count <= 0) return;
        view.Blueprint = ((index % count) + count) % count;
    }

    /// <summary>Count of an item in the buffer (safe on any peer).</summary>
    public int BufferCount(string item) => ValidView ? View.Count(item) : 0;

    /// <summary>Human-readable label for what this machine is currently set to do.</summary>
    public string BlueprintLabel() {
        var view = View;
        var def = view.Def;
        if (def.Kind == MachineKind.Stockpile) return def.RawItemAt(view.Blueprint) ?? "(nothing)";
        var recipe = def.RecipeAt(view.Blueprint);
        return recipe != null ? recipe.Describe() : "(passive)";
    }

    // ------------------------------------------------------------------ spawning
    /// <summary>
    /// Instantiate a machine of the given kind at a position (the spawner owns it).
    /// Used both by the build hammer's placement and directly by the E2E harness.
    /// </summary>
    public static Machine? Spawn(MachineKind kind, Vector3 pos, Quaternion rot, string tag = "") {
        var def = MachineCatalog.ForKind(kind);
        var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(def.PrefabName) : null;
        if (prefab == null) {
            Plugin.Logger.LogError($"Machine.Spawn: prefab '{def.PrefabName}' is not registered");
            return null;
        }
        var go = Object.Instantiate(prefab, pos, rot);
        var nview = go.GetComponent<ZNetView>();
        if (nview == null || !nview.IsValid()) {
            Plugin.Logger.LogError($"Machine.Spawn: '{def.PrefabName}' has no valid ZNetView");
            return null;
        }
        // Kind is derived from the prefab hash (MachineView.Def), so nothing to set
        // here beyond the initial blueprint and optional tag.
        var view = new MachineView(nview);
        view.Blueprint = 0;
        if (!string.IsNullOrEmpty(tag)) view.Tag = tag;
        return go.GetComponent<Machine>();
    }

    /// <summary>Find the first live machine matching a tag (test/discovery helper).</summary>
    public static Machine? FindByTag(string tag) {
        foreach (var m in All) {
            if (m != null && m.ValidView && m.View.Tag == tag) return m;
        }
        return null;
    }
}
