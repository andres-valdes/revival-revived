using System.Collections.Generic;
using UnityEngine;

namespace RevivalRevived.Components;

/// <summary>
/// Turns a freshly-spawned tombstone into a downed-player "revive marker": strips
/// the loot/despawn scripts so it is inert, recolors its accent green, and
/// attaches a <see cref="ReviveInteractable"/>. The tombstone keeps its mesh,
/// collider, Rigidbody and Floating, so it drops in with the vanilla pop, bobs
/// on water, and its position replicates like any networked object.
///
/// The accent colour is not static: it lerps from revive-green back to the
/// tombstone's original (red) accent as the revive window runs out, so the
/// marker visually "becomes" a grave right as it converts to the real one.
///
/// Conversion is idempotent and ZDO-driven: it runs on every client whose
/// tombstone ZDO has <see cref="DownedState.s_isDownedMarker"/> set (owner spawns
/// it directly; remotes convert from a <c>TombStone.Start</c> postfix).
/// </summary>
public class DownedMarker : MonoBehaviour {
    /// <summary>Revive-window accent colour at full time remaining.</summary>
    public static readonly Color ReviveGreen = new(0.25f, 1f, 0.35f);

    private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");
    private static readonly int ColorId = Shader.PropertyToID("_Color");

    private struct TintedMaterial {
        public Material Mat;
        public bool HasEmission;
        public Color OrigEmission;
        public float EmissionIntensity;
        public bool HasColor;
        public Color OrigColor;
    }

    private readonly List<TintedMaterial> m_tinted = new();
    private readonly List<(Light light, Color orig)> m_lights = new();
    private ZNetView? m_nview;

    public int TintedLights => m_lights.Count;
    public int TintedMaterials => m_tinted.Count;

    /// <summary>Ember/glow effect objects disabled on this marker (test hook).</summary>
    public int DisabledEffects { get; private set; }

    /// <summary>0 = fully green (window fresh), 1 = original grave red (window elapsed). Test hook.</summary>
    public float CurrentBlend { get; private set; }

    public static void Convert(TombStone tomb) {
        if (tomb == null) return;
        var go = tomb.gameObject;
        if (go.GetComponent<DownedMarker>() != null) return; // idempotent
        go.AddComponent<DownedMarker>().StripAndTint(tomb);
    }

    private void StripAndTint(TombStone tomb) {
        m_nview = GetComponent<ZNetView>();

        // Show the downed player's name like a vanilla grave would. TombStone
        // normally does this in Start(), but on the spawning client we convert
        // (and destroy the script) before Start ever runs, leaving the prefab
        // default "GRAVE".
        if (tomb.m_worldText != null && m_nview != null && m_nview.IsValid()) {
            var ownerName = m_nview.GetZDO().GetString(ZDOVars.s_ownerName);
            if (!string.IsNullOrEmpty(ownerName)) tomb.m_worldText.text = ownerName;
        }

        // Stop the loot/despawn behaviour: cancel the repeating despawn check and
        // remove the TombStone + Container scripts (both are Hoverable/Interactable
        // and would otherwise compete with our ReviveInteractable).
        tomb.CancelInvoke();
        var container = GetComponent<Container>();
        if (container != null) Destroy(container);
        Destroy(tomb);

        // The grave's ember particles and glow flare only belong on a REAL
        // (unrevivable) tombstone -- their absence marks this as revivable, and
        // their presence on the replacing grave signals the window has closed.
        DisableGraveEffects();

        CaptureAccents();
        ApplyBlend(0f);

        if (GetComponent<ReviveInteractable>() == null) {
            gameObject.AddComponent<ReviveInteractable>();
        }

        Plugin.Logger.LogInfo($"DownedMarker: converted tombstone -> green marker (lights={TintedLights}, mats={TintedMaterials})");
    }

    /// <summary>Turn off ember particle systems and flare/glow children.</summary>
    private void DisableGraveEffects() {
        foreach (var ps in GetComponentsInChildren<ParticleSystem>(includeInactive: true)) {
            ps.Stop(withChildren: true, ParticleSystemStopBehavior.StopEmittingAndClear);
            ps.gameObject.SetActive(false);
            DisabledEffects++;
        }
        foreach (var t in GetComponentsInChildren<Transform>(includeInactive: true)) {
            var n = t.name.ToLowerInvariant();
            if ((n.Contains("flare") || n.Contains("glow")) && t.gameObject.activeSelf) {
                t.gameObject.SetActive(false);
                DisabledEffects++;
            }
        }
    }

    /// <summary>Find the red accent sources and remember their original colours.</summary>
    private void CaptureAccents() {
        foreach (var light in GetComponentsInChildren<Light>(includeInactive: true)) {
            m_lights.Add((light, light.color));
        }

        foreach (var renderer in GetComponentsInChildren<Renderer>(includeInactive: true)) {
            // Use .materials (instance copies) so we don't mutate shared assets.
            foreach (var mat in renderer.materials) {
                var entry = new TintedMaterial { Mat = mat };
                if (mat.HasProperty(EmissionColorId)) {
                    var cur = mat.GetColor(EmissionColorId);
                    // Only accent materials actually emit; preserve brightness.
                    if (cur.maxColorComponent > 0.05f) {
                        entry.HasEmission = true;
                        entry.OrigEmission = cur;
                        entry.EmissionIntensity = cur.maxColorComponent;
                    }
                }
                if (mat.HasProperty(ColorId)) {
                    var cur = mat.GetColor(ColorId);
                    // Clearly-red tint materials (the accent); leave stone grey alone.
                    if (cur.r > 0.4f && cur.r > cur.g * 1.5f && cur.r > cur.b * 1.5f) {
                        entry.HasColor = true;
                        entry.OrigColor = cur;
                    }
                }
                if (entry.HasEmission || entry.HasColor) m_tinted.Add(entry);
            }
        }
    }

    /// <summary>blend 0 = green, 1 = original red.</summary>
    private void ApplyBlend(float blend) {
        CurrentBlend = blend;
        foreach (var e in m_tinted) {
            if (e.Mat == null) continue;
            if (e.HasEmission) {
                var green = ReviveGreen * e.EmissionIntensity;
                e.Mat.SetColor(EmissionColorId, Color.Lerp(green, e.OrigEmission, blend));
                e.Mat.EnableKeyword("_EMISSION");
            }
            if (e.HasColor) {
                e.Mat.SetColor(ColorId, Color.Lerp(ReviveGreen, e.OrigColor, blend));
            }
        }
        foreach (var (light, orig) in m_lights) {
            if (light != null) light.color = Color.Lerp(ReviveGreen, orig, blend);
        }
    }

    private void Update() {
        if (m_nview == null || !m_nview.IsValid()) return;
        // Blend from green back to the grave's own red as the window elapses.
        //
        // The clock is read from the LINKED PLAYER's ZDO (its owner maintains it,
        // including the pause-while-channeling shift). The marker ZDO must have a
        // single writer -- the channeling reviver publishing progress -- so the
        // window clock cannot live here without ZDO revision fights.
        var zdo = m_nview.GetZDO();
        var playerZdoId = zdo.GetZDOID(DownedState.s_playerZDOID);
        var playerZdo = playerZdoId != ZDOID.None ? ZDOMan.instance.GetZDO(playerZdoId) : null;
        var downedTime = playerZdo != null
            ? playerZdo.GetFloat(DownedState.s_downedTime)
            : zdo.GetFloat(DownedState.s_downedTime); // fallback: spawn-time value
        var elapsed = (float)ZNet.instance.GetTimeSeconds() - downedTime;
        ApplyBlend(Mathf.Clamp01(elapsed / DownedState.ReviveWindow));
    }

    /// <summary>True if the marker recoloured at least one accent source (test hook).</summary>
    public bool IsGreen() => (TintedLights > 0 || TintedMaterials > 0) && CurrentBlend < 0.5f;
}
