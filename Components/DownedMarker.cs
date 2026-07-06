using System.Collections.Generic;
using UnityEngine;

namespace ReviveAllies.Components;

/// <summary>
/// The revive-marker component (every client that instantiates the prefab). Its
/// instance side is presentation only: recolour the tombstone's red accent to
/// revive-green and lerp it back to red as the window runs out, show the downed
/// player's name, and -- once the real grave has taken its place -- hide itself
/// gap-free and destroy the ZDO. Its static side is the marker's lifecycle
/// operations (mark-replaced / crumble / destroy).
///
/// The marker's data is <see cref="DownedMarkerView"/>; the prefab and spawning
/// are <see cref="MarkerPrefab"/>.
/// </summary>
public class DownedMarker : MonoBehaviour {
    /// <summary>Revive-window accent colour at full time remaining.</summary>
    public static readonly Color ReviveGreen = new(0.25f, 1f, 0.35f);

    // ---- marker lifecycle operations (statics) -----------------------------

    /// <summary>Effect objects created by the last marker crumble (test hook).</summary>
    public static int LastCrumbleEffectCount { get; private set; }

    /// <summary>Total crumbles this session (test hook; use deltas across an action).</summary>
    public static int CrumbleEvents { get; private set; }

    /// <summary>
    /// The real grave has spawned in this marker's place: flag the marker ZDO so
    /// every client swaps its presentation locally, gap-free. The instance's
    /// Update handles hide + delayed destroy.
    /// </summary>
    public static void MarkReplaced(GameObject? marker) {
        var nview = Owned(marker);
        if (nview == null) return;
        var m = new DownedMarkerView(nview);
        m.ReplacedByGrave = true;
    }

    /// <summary>
    /// Despawn the marker the way an emptied grave does: play the tombstone's own
    /// crumble effect (a networked vfx, so it covers the disappearance on every
    /// client) and destroy it. Used on revive, and on death when no grave spawns.
    /// </summary>
    public static void Crumble(GameObject? marker) {
        if (marker == null) return;
        var effect = GraveCrumbleEffect();
        if (effect != null) {
            var created = effect.Create(marker.transform.position, marker.transform.rotation);
            LastCrumbleEffectCount = created?.Length ?? 0;
            CrumbleEvents++;
        }
        DestroyMarker(marker);
    }

    /// <summary>Crumble the marker linked from a player's ZDO and clear the link.</summary>
    public static void CrumbleLinked(ZNetView playerNview) {
        var markerId = new DownedStateMachineView(playerNview).Marker;
        if (markerId == ZDOID.None) return;
        var state = new DownedStateMachineView(playerNview);
        state.Marker = ZDOID.None;
        Crumble(ZNetScene.instance.FindInstance(markerId));
    }

    /// <summary>Destroy a marker, claiming ownership first (an orphaned marker is owned by the server).</summary>
    public static void DestroyMarker(GameObject? marker) {
        var nview = Owned(marker);
        nview?.Destroy();
    }

    /// <summary>The marker's ZNetView, ownership claimed, or null if invalid.</summary>
    private static ZNetView? Owned(GameObject? marker) {
        var nview = marker != null ? marker.GetComponent<ZNetView>() : null;
        if (nview == null || !nview.IsValid()) return null;
        if (!nview.IsOwner()) nview.ClaimOwnership();
        return nview;
    }

    private static EffectList? s_crumbleEffect;

    /// <summary>The vanilla tombstone's remove-effect (what plays when an emptied grave despawns).</summary>
    private static EffectList? GraveCrumbleEffect() {
        if (s_crumbleEffect != null) return s_crumbleEffect;
        var prefab = ZNetScene.instance != null ? MarkerPrefab.FindTombstonePrefab(ZNetScene.instance) : null;
        var tomb = prefab != null ? prefab.GetComponent<TombStone>() : null;
        if (tomb != null && tomb.m_removeEffect != null && tomb.m_removeEffect.m_effectPrefabs.Length > 0) {
            s_crumbleEffect = tomb.m_removeEffect;
        } else {
            Plugin.Logger.LogWarning("DownedMarker: no grave crumble effect found");
        }
        return s_crumbleEffect;
    }

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

    /// <summary>0 = fully green (window fresh), 1 = original grave red (window elapsed). Test hook.</summary>
    public float CurrentBlend { get; private set; }

    private void Awake() {
        m_nview = GetComponent<ZNetView>();
        CaptureAccents();
        ApplyBlend(0f);
    }

    private void Start() {
        // Show the downed player's name like a vanilla grave would (the ZDO is
        // populated by the time Start runs, on the spawner and on remotes).
        if (m_nview == null || !m_nview.IsValid()) return;
        var ownerName = new DownedMarkerView(m_nview).OwnerName;
        if (string.IsNullOrEmpty(ownerName)) return;
        var worldText = GetComponentInChildren<TMPro.TMP_Text>(true);
        if (worldText != null) worldText.text = ownerName;
    }

    private void Update() {
        if (m_nview == null || !m_nview.IsValid()) return;
        var marker = new DownedMarkerView(m_nview);

        // The real grave took our place: hide once it is visible here, destroy
        // the ZDO after a grace period. No gradient/interaction from here on.
        if (marker.ReplacedByGrave) {
            UpdateReplaced();
            return;
        }

        // Blend from green back to the grave's own red as the window elapses.
        // The clock is read from the LINKED PLAYER's ZDO (its owner maintains it,
        // including the pause-while-channeling shift); fall back to the marker's
        // own spawn-time value when the player ZDO is out of range.
        var playerZdoId = marker.LinkedPlayer;
        var playerZdo = playerZdoId != ZDOID.None ? ZDOMan.instance.GetZDO(playerZdoId) : null;
        var downedTime = playerZdo != null
            ? new DownedStateMachineView(playerZdo).DownedTime
            : marker.DownedTime;
        var elapsed = (float)ZNet.instance.GetTimeSeconds() - downedTime;
        ApplyBlend(Mathf.Clamp01(elapsed / Plugin.ReviveWindow));
    }

    // ---- Accent recolouring (green <-> grave red) --------------------------

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

    /// <summary>True if the marker recoloured at least one accent source (test hook).</summary>
    public bool IsGreen() => (TintedLights > 0 || TintedMaterials > 0) && CurrentBlend < 0.5f;

    // ---- Replaced-by-grave handoff (local presentation, then destroy) ------

    /// <summary>Local time we first observed the replaced flag; -1 = not replaced.</summary>
    private float m_replacedSince = -1f;
    private bool m_hiddenForReplace;

    /// <summary>Local renderers/colliders/lights are disabled, awaiting ZDO destroy (test hook).</summary>
    public bool HiddenForReplace => m_hiddenForReplace;

    /// <summary>Hide as soon as the grave is seen, or unconditionally after this long.</summary>
    private const float ReplaceHideTimeout = 4f;
    /// <summary>The client that flagged the replace destroys the ZDO after this long.</summary>
    private const float ReplaceDestroyDelay = 5f;
    /// <summary>Any client cleans up a lingering replaced marker after this long (flagger vanished).</summary>
    private const float ReplaceDestroyFailsafe = 10f;

    private void UpdateReplaced() {
        if (m_replacedSince < 0f) m_replacedSince = Time.time;
        float elapsed = Time.time - m_replacedSince;

        // Swap locally only once THIS client can see the real grave standing in
        // for us -- that is what makes the handoff gap-free everywhere.
        if (!m_hiddenForReplace && (elapsed > ReplaceHideTimeout || GraveNearby())) {
            HideLocally();
        }

        if (m_nview!.IsOwner()) {
            if (elapsed > ReplaceDestroyDelay) m_nview.Destroy();
        } else if (elapsed > ReplaceDestroyFailsafe) {
            m_nview.ClaimOwnership();
            m_nview.Destroy();
        }
    }

    private bool GraveNearby() {
        foreach (var tomb in Object.FindObjectsOfType<TombStone>()) {
            if ((tomb.transform.position - transform.position).sqrMagnitude < 9f) return true;
        }
        return false;
    }

    private void HideLocally() {
        m_hiddenForReplace = true;
        foreach (var r in GetComponentsInChildren<Renderer>()) r.enabled = false;
        foreach (var c in GetComponentsInChildren<Collider>()) c.enabled = false;
        foreach (var l in GetComponentsInChildren<Light>()) l.enabled = false;
    }
}
