using System.Collections.Generic;
using Automations.Domain;
using UnityEngine;

namespace Automations.Components;

/// <summary>
/// Draws every pipe as the game's own station-connection effect -- the scrolling
/// dotted line that links a workbench to its extensions. We borrow that exact
/// prefab (<see cref="Prefabs.MachinePrefabs.ConnectionPrefab"/>) and stretch one
/// instance per pipe from the source machine to its target, oriented source->target
/// so the texture appears to flow in the direction items travel.
///
/// Lines show while the player holds the wiring key (or always, per config). Purely
/// cosmetic and local -- it reads the replicated <see cref="MachineView.Outputs"/>
/// and never writes anything.
/// </summary>
public class PipeRenderer : MonoBehaviour {
    private static PipeRenderer? s_instance;

    /// <summary>Create the singleton renderer once the scene is up.</summary>
    public static void EnsureExists() {
        if (s_instance != null) return;
        var go = new GameObject("Automations_PipeRenderer");
        DontDestroyOnLoad(go);
        s_instance = go.AddComponent<PipeRenderer>();
    }

    // One line instance per directed pipe, keyed "srcUid->dstUid".
    private readonly Dictionary<string, GameObject> m_lines = new();
    private readonly List<string> m_stale = new();

    private void LateUpdate() {
        var template = Prefabs.MachinePrefabs.ConnectionPrefab;
        if (template == null || Player.m_localPlayer == null) { HideAll(); return; }

        bool show = Plugin.AlwaysShowPipes.Value || Input.GetKey(Plugin.WiringKey.Value);
        if (!show) { HideAll(); return; }

        var seen = new HashSet<string>();
        foreach (var machine in Machine.All) {
            if (machine == null || !machine.ValidView) continue;
            var view = machine.View;
            var origin = ConnectionPoint(machine.transform.position);

            foreach (var targetId in view.Outputs) {
                var go = ZNetScene.instance != null ? ZNetScene.instance.FindInstance(targetId) : null;
                if (go == null) continue;
                var key = machine.ZdoId + "->" + targetId;
                seen.Add(key);

                var target = ConnectionPoint(go.transform.position);
                var line = GetOrCreateLine(key, template);
                if (line == null) continue;
                StretchLine(line, origin, target);
            }
        }

        // Retire lines whose pipe is gone or out of range.
        m_stale.Clear();
        foreach (var kv in m_lines) if (!seen.Contains(kv.Key)) m_stale.Add(kv.Key);
        foreach (var key in m_stale) { if (m_lines[key] != null) Destroy(m_lines[key]); m_lines.Remove(key); }
    }

    private GameObject? GetOrCreateLine(string key, GameObject template) {
        if (m_lines.TryGetValue(key, out var existing) && existing != null) return existing;
        var line = Instantiate(template);
        line.SetActive(true);
        m_lines[key] = line;
        return line;
    }

    /// <summary>Position, orient and scale a connection instance from a to b (mirrors StationExtension).</summary>
    private static void StretchLine(GameObject line, Vector3 a, Vector3 b) {
        var delta = b - a;
        if (delta.sqrMagnitude < 0.0001f) return;
        line.transform.position = a;
        line.transform.rotation = Quaternion.LookRotation(delta.normalized);
        line.transform.localScale = new Vector3(1f, 1f, delta.magnitude);
    }

    private static Vector3 ConnectionPoint(Vector3 basePos) => basePos + Vector3.up * 0.6f;

    private void HideAll() {
        if (m_lines.Count == 0) return;
        foreach (var kv in m_lines) if (kv.Value != null) Destroy(kv.Value);
        m_lines.Clear();
    }

    private void OnDestroy() { HideAll(); if (s_instance == this) s_instance = null; }
}
