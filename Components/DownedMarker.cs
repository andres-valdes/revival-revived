using System.Collections.Generic;
using UnityEngine;
using ZdoTyped;

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
/// tombstone ZDO has <see cref="View.IsDownedMarker"/> set (owner spawns
/// it directly; remotes convert from a <c>TombStone.Start</c> postfix).
/// </summary>
public partial class DownedMarker : MonoBehaviour {
    /// <summary>
    /// Typed view over the marker's ZDO. Written by its single writer -- the
    /// channeling reviver (progress) -- plus the immutable identity fields set
    /// once at spawn. Key names preserve the established wire format.
    /// </summary>
    [ZdoTyped.ZdoSchema("RevivalRevived")]
    public partial struct View {
        /// <summary>Distinguishes the revive marker from a real loot grave.</summary>
        [ZdoTyped.ZdoField(Name = "isDownedMarker")] public partial bool IsDownedMarker { get; set; }

        /// <summary>Peer-authoritative channel progress 0-1, written by the reviving client.</summary>
        [ZdoTyped.ZdoField(Name = "reviveProgress")] public partial float ReviveProgress { get; set; }

        /// <summary>Stable PlayerID of the downed player (survives logout, unlike the character ZDOID).</summary>
        [ZdoTyped.ZdoField(Name = "ownerPlayerID")] public partial long OwnerPlayerId { get; set; }

        /// <summary>Spawn-time clock; gradient fallback when the player ZDO is gone.</summary>
        [ZdoTyped.ZdoField(Name = "downedTime")] public partial float DownedTime { get; set; }

        /// <summary>Cross-link back to the downed player's character ZDO.</summary>
        [ZdoTyped.ZdoField(Name = "playerZDOID")] public partial ZDOID Player { get; set; }

        /// <summary>Vanilla world-text key, shared with real graves.</summary>
        [ZdoTyped.ZdoField(Name = "ownerName", NoPrefix = true)] public partial string OwnerName { get; set; }
    }

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

    /// <summary>Ember/glow objects deleted from the prefab template at build time (test hook).</summary>
    public static int TemplateEffectsRemoved { get; private set; }

    /// <summary>0 = fully green (window fresh), 1 = original grave red (window elapsed). Test hook.</summary>
    public float CurrentBlend { get; private set; }

    // =====================================================================
    //  Marker lifecycle (spawn / find / destroy)
    // =====================================================================

    /// <summary>Upward launch velocity applied to the last spawned marker (test hook).</summary>
    public static float LastPopVelY { get; private set; }

    /// <summary>
    /// Spawn the green marker for a freshly-downed player (owner only) and
    /// cross-link it to the player via ZDO. Instantiates OUR registered prefab;
    /// remote clients instantiate the same prefab from the replicated ZDO.
    /// </summary>
    public static void Spawn(Player player) {
        var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(PrefabName) : null;
        if (prefab == null) {
            Plugin.Logger.LogError($"DownedMarker.Spawn: prefab '{PrefabName}' is not registered");
            return;
        }

        var go = Object.Instantiate(prefab, player.GetCenterPoint(), player.transform.rotation);
        var nview = go.GetComponent<ZNetView>();
        if (nview == null || !nview.IsValid()) {
            Plugin.Logger.LogError("DownedMarker.Spawn: marker has no valid ZNetView");
            return;
        }

        var markerZdo = nview.GetZdo<View>();
        var playerZdo = player.m_nview.GetZdo<DownedPlayerZdo>();

        markerZdo.IsDownedMarker = true; // distinguishes from real graves
        markerZdo.Player = player.m_nview.GetZDO().m_uid;
        markerZdo.OwnerPlayerId = player.GetPlayerID(); // stable across rejoin
        markerZdo.OwnerName = player.GetPlayerName();   // world text
        markerZdo.DownedTime = (float)ZNet.instance.GetTimeSeconds(); // fallback clock

        playerZdo.Marker = nview.GetZDO().m_uid;

        // Vanilla tombstone "drop-in" pop (TombStone.Setup normally does this;
        // our marker has no TombStone script).
        var body = go.GetComponent<Rigidbody>();
        if (body != null) body.linearVelocity = new Vector3(0f, 5f, 0f);
        LastPopVelY = body != null ? body.linearVelocity.y : 0f;

        Plugin.Logger.LogInfo($"Spawned downed marker for {player.GetPlayerName()}, ZDOID {nview.GetZDO().m_uid}");
    }

    /// <summary>
    /// Find the marker belonging to a stable PlayerID (used to detect the orphan
    /// left behind when a downed player disconnects).
    /// </summary>
    public static GameObject? FindFor(long playerId) {
        if (playerId == 0L) return null;
        foreach (var dm in Object.FindObjectsOfType<DownedMarker>()) {
            var nv = dm.GetComponent<ZNetView>();
            if (nv == null || !nv.IsValid()) continue;
            if (nv.GetZdo<View>().OwnerPlayerId == playerId) return dm.gameObject;
        }
        return null;
    }

    /// <summary>Destroy the marker linked from a player's typed view and clear the link.</summary>
    public static void DestroyLinkedMarker(ref DownedPlayerZdo playerZdo) {
        var markerId = playerZdo.Marker;
        if (markerId == ZDOID.None) return;

        playerZdo.Marker = ZDOID.None;
        DestroyMarker(ZNetScene.instance.FindInstance(markerId));
    }

    /// <summary>
    /// Destroy a marker tombstone, claiming ownership first: after a disconnect
    /// (or a reviver's progress claim) the marker may be owned elsewhere, and a
    /// non-owner Destroy silently fails to replicate.
    /// </summary>
    public static void DestroyMarker(GameObject? go) {
        if (go == null) return;
        var nview = go.GetComponent<ZNetView>();
        if (nview == null || !nview.IsValid()) return;
        if (!nview.IsOwner()) nview.ClaimOwnership();
        nview.Destroy();
    }

    // =====================================================================
    //  Prefab registration (our own networked prefab -- no tombstone conversion)
    // =====================================================================

    /// <summary>Networked prefab name; must be identical (and registered) on every client.</summary>
    public const string PrefabName = "RevivalRevived_DownedMarker";

    private static GameObject? s_prefabHolder;
    private static GameObject? s_prefabTemplate;

    /// <summary>
    /// Build (once) and register the marker prefab with a ZNetScene. Called from
    /// the ZNetScene.Awake postfix on every client, so remote instances are
    /// instantiated directly from this prefab via the ZDO prefab hash -- no
    /// spawn-a-tombstone-and-convert hack, no TombStone.Start patch.
    ///
    /// The template is the vanilla player tombstone cloned under an inactive
    /// holder (so no Awake runs), with the loot/despawn scripts removed and our
    /// components pre-attached.
    /// </summary>
    public static void RegisterPrefab(ZNetScene scene) {
        if (s_prefabTemplate == null) {
            var original = FindTombstonePrefab(scene);
            if (original == null) {
                Plugin.Logger.LogError("DownedMarker: no TombStone prefab found to derive the marker from");
                return;
            }

            s_prefabHolder = new GameObject("RevivalRevived_Prefabs");
            s_prefabHolder.SetActive(false); // children never run Awake here
            Object.DontDestroyOnLoad(s_prefabHolder);

            var template = Object.Instantiate(original, s_prefabHolder.transform);
            template.name = PrefabName;

            // Curate the template ONCE so the prefab contains exactly what the
            // marker is -- instances never add or remove anything.
            // (A fully from-scratch prefab would need an AssetBundle: the stone
            // model, materials, collider and Floating tuning only exist in the
            // vanilla prefab, so we derive and curate instead.)
            var tomb = template.GetComponent<TombStone>();
            if (tomb != null) Object.DestroyImmediate(tomb);
            var container = template.GetComponent<Container>();
            if (container != null) Object.DestroyImmediate(container);
            RemoveGraveEffects(template);

            // Our behaviour, baked into the prefab.
            template.AddComponent<DownedMarker>();
            template.AddComponent<ReviveInteractable>();

            s_prefabTemplate = template;
            Plugin.Logger.LogInfo($"DownedMarker: built prefab '{PrefabName}' from '{original.name}'");
        }

        var hash = PrefabName.GetStableHashCode();
        if (!scene.m_namedPrefabs.ContainsKey(hash)) {
            scene.m_prefabs.Add(s_prefabTemplate);
            scene.m_namedPrefabs.Add(hash, s_prefabTemplate);
            Plugin.Logger.LogInfo($"DownedMarker: registered prefab '{PrefabName}' with ZNetScene");
        }
    }

    private static GameObject? FindTombstonePrefab(ZNetScene scene) {
        var byName = scene.GetPrefab("Player_tombstone");
        if (byName != null && byName.GetComponent<TombStone>() != null) return byName;
        foreach (var p in scene.m_prefabs) {
            if (p != null && p.GetComponent<TombStone>() != null) return p;
        }
        return null;
    }

    // =====================================================================
    //  Instance behaviour (runs on every client that instantiates the prefab)
    // =====================================================================

    private void Awake() {
        m_nview = GetComponent<ZNetView>();
        CaptureAccents();
        ApplyBlend(0f);
    }

    private void Start() {
        // Show the downed player's name like a vanilla grave would (the ZDO is
        // populated by the time Start runs, on the spawner and on remotes).
        if (!m_nview.TryGetZdo<View>(out var view)) return;
        var ownerName = view.OwnerName;
        if (string.IsNullOrEmpty(ownerName)) return;
        var worldText = GetComponentInChildren<TMPro.TMP_Text>(true);
        if (worldText != null) worldText.text = ownerName;
    }

    /// <summary>
    /// Delete ember particle systems and flare/glow children from the prefab
    /// TEMPLATE. The grave effects belong only on a real (unrevivable) grave --
    /// the marker prefab simply doesn't have them, so instances never need to
    /// disable anything.
    /// </summary>
    private static void RemoveGraveEffects(GameObject template) {
        foreach (var ps in template.GetComponentsInChildren<ParticleSystem>(includeInactive: true)) {
            if (ps != null && ps.gameObject != template) {
                Object.DestroyImmediate(ps.gameObject);
                TemplateEffectsRemoved++;
            }
        }
        foreach (var t in template.GetComponentsInChildren<Transform>(includeInactive: true)) {
            if (t == null || t.gameObject == template) continue;
            var n = t.name.ToLowerInvariant();
            if (n.Contains("flare") || n.Contains("glow")) {
                Object.DestroyImmediate(t.gameObject);
                TemplateEffectsRemoved++;
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
        var view = m_nview.GetZdo<View>();
        var playerZdoId = view.Player;
        var playerZdo = playerZdoId != ZDOID.None ? ZDOMan.instance.GetZDO(playerZdoId) : null;
        var downedTime = playerZdo != null
            ? playerZdo.GetZdo<DownedPlayerZdo>().DownedTime
            : view.DownedTime; // fallback: spawn-time value
        var elapsed = (float)ZNet.instance.GetTimeSeconds() - downedTime;
        ApplyBlend(Mathf.Clamp01(elapsed / Plugin.ReviveWindow));
    }

    /// <summary>True if the marker recoloured at least one accent source (test hook).</summary>
    public bool IsGreen() => (TintedLights > 0 || TintedMaterials > 0) && CurrentBlend < 0.5f;
}
