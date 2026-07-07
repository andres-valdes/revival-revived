using Automations.Domain;
using UnityEngine;

namespace Automations.Components;

/// <summary>
/// The blueprint auto-crafter -- the one machine that is a bespoke prefab, because
/// no vanilla piece assembles items on its own. It holds its own buffer, and each
/// tick (on the owner) applies its selected blueprint: consume the recipe's inputs,
/// produce its output. Inputs arrive via pipes into <see cref="AssemblerPort"/>;
/// outputs leave via pipes. Its blueprint is cycled by interacting with it.
///
/// A small "working" effect object is toggled while it can actively craft, so it
/// reads as alive the way the vanilla stations do.
/// </summary>
public class Assembler : MonoBehaviour {
    private ZNetView m_nview = null!;
    private float m_nextTick;
    private GameObject? m_workingFx;
    private bool m_fxSearched;

    private void Awake() {
        m_nview = GetComponent<ZNetView>();
        m_nextTick = Time.time + Random.Range(0f, Plugin.TickInterval);
    }

    public bool ValidView => m_nview != null && m_nview.IsValid();
    public AssemblerView View => new(m_nview);

    public void CycleBlueprint() {
        if (!ValidView) return;
        if (!m_nview.IsOwner()) m_nview.ClaimOwnership();
        var v = View;
        v.Blueprint = (v.Blueprint + 1) % Blueprints.All.Count;
    }

    private void Update() {
        if (!ValidView) return;
        bool canWork = HasWork();
        DriveWorkingFx(canWork && m_nview.IsOwner());

        if (!m_nview.IsOwner()) return;
        if (Time.time < m_nextTick) return;
        m_nextTick = Time.time + Plugin.TickInterval;
        Process();
    }

    /// <summary>True when the current blueprint's inputs are present and output has room.</summary>
    private bool HasWork() {
        if (!ValidView) return false;
        var v = View;
        var recipe = v.Recipe;
        foreach (var need in recipe.Inputs) if (v.Count(need.Key) < need.Value) return false;
        foreach (var made in recipe.Outputs) if (v.FreeSpace(made.Key) < made.Value) return false;
        return true;
    }

    private void Process() {
        var v = View;
        for (int batch = 0; batch < Plugin.ProcessPerTick; batch++) {
            var recipe = v.Recipe;
            foreach (var need in recipe.Inputs) if (v.Count(need.Key) < need.Value) return;
            foreach (var made in recipe.Outputs) if (v.FreeSpace(made.Key) < made.Value) return;
            foreach (var need in recipe.Inputs) v.Remove(need.Key, need.Value);
            foreach (var made in recipe.Outputs) v.Add(made.Key, made.Value);
        }
    }

    /// <summary>Toggle a fire/ember/glow child on the derived prefab to signal activity.</summary>
    private void DriveWorkingFx(bool on) {
        if (!m_fxSearched) {
            m_fxSearched = true;
            foreach (var t in GetComponentsInChildren<Transform>(true)) {
                var n = t.name.ToLowerInvariant();
                if (t.gameObject != gameObject && (n.Contains("fire") || n.Contains("ember") || n.Contains("enabled") || n.Contains("light"))) {
                    m_workingFx = t.gameObject;
                    break;
                }
            }
        }
        if (m_workingFx != null && m_workingFx.activeSelf != on) m_workingFx.SetActive(on);
    }
}
