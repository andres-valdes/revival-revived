using UnityEngine;

namespace RevivalRevived.Components;

/// <summary>
/// Turns a freshly-spawned tombstone into a downed-player "revive marker": strips
/// the loot/despawn scripts so it is inert, tints its accent green (vanilla graves
/// glow red), and attaches a <see cref="ReviveInteractable"/>. The tombstone keeps
/// its mesh, collider, Rigidbody and Floating, so it still bobs on water and its
/// position replicates like any networked object.
///
/// Conversion is idempotent and ZDO-driven: it runs on every client whose
/// tombstone ZDO has <see cref="DownedState.s_isDownedMarker"/> set (owner spawns
/// it directly; remotes convert from a <c>TombStone.Start</c> postfix).
/// </summary>
public class DownedMarker : MonoBehaviour {
    /// <summary>Revive-window accent colour.</summary>
    public static readonly Color ReviveGreen = new(0.25f, 1f, 0.35f);

    private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");
    private static readonly int ColorId = Shader.PropertyToID("_Color");

    public int TintedLights { get; private set; }
    public int TintedMaterials { get; private set; }

    public static void Convert(TombStone tomb) {
        if (tomb == null) return;
        var go = tomb.gameObject;
        if (go.GetComponent<DownedMarker>() != null) return; // idempotent
        go.AddComponent<DownedMarker>().StripAndTint(tomb);
    }

    private void StripAndTint(TombStone tomb) {
        // Stop the loot/despawn behaviour: cancel the repeating despawn check and
        // remove the TombStone + Container scripts (both are Hoverable/Interactable
        // and would otherwise compete with our ReviveInteractable).
        tomb.CancelInvoke();
        var container = GetComponent<Container>();
        if (container != null) Destroy(container);
        Destroy(tomb);

        TintGreen();

        if (GetComponent<ReviveInteractable>() == null) {
            gameObject.AddComponent<ReviveInteractable>();
        }

        Plugin.Logger.LogInfo($"DownedMarker: converted tombstone -> green marker (lights={TintedLights}, mats={TintedMaterials})");
    }

    private void TintGreen() {
        foreach (var light in GetComponentsInChildren<Light>(includeInactive: true)) {
            light.color = ReviveGreen;
            TintedLights++;
        }

        foreach (var renderer in GetComponentsInChildren<Renderer>(includeInactive: true)) {
            // Use .materials (instance copies) so we don't mutate shared assets.
            foreach (var mat in renderer.materials) {
                bool tinted = false;
                if (mat.HasProperty(EmissionColorId)) {
                    var cur = mat.GetColor(EmissionColorId);
                    // Only recolour materials that actually emit (the red accent),
                    // preserve their brightness.
                    if (cur.maxColorComponent > 0.05f) {
                        mat.SetColor(EmissionColorId, ReviveGreen * cur.maxColorComponent);
                        mat.EnableKeyword("_EMISSION");
                        tinted = true;
                    }
                }
                if (mat.HasProperty(ColorId)) {
                    var cur = mat.GetColor(ColorId);
                    // Recolour clearly-red tint materials (the accent), leave stone grey.
                    if (cur.r > 0.4f && cur.r > cur.g * 1.5f && cur.r > cur.b * 1.5f) {
                        mat.SetColor(ColorId, ReviveGreen);
                        tinted = true;
                    }
                }
                if (tinted) TintedMaterials++;
            }
        }
    }

    /// <summary>True if the marker recoloured at least one accent source (test hook).</summary>
    public bool IsGreen() => TintedLights > 0 || TintedMaterials > 0;
}
