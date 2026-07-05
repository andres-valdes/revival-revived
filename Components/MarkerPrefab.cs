using UnityEngine;

namespace ReviveAllies.Components;

/// <summary>
/// The revive-marker networked prefab: builds and registers it (once, from the
/// vanilla tombstone), spawns instances for downed players, and finds orphans by
/// stable PlayerID. Purely the factory/registry -- the marker's data lives in
/// <see cref="MarkerState"/> and its visuals in <see cref="DownedMarker"/>.
/// </summary>
public static class MarkerPrefab {
    /// <summary>Networked prefab name; must be identical (and registered) on every client.</summary>
    public const string PrefabName = "RevivalRevived_DownedMarker";

    /// <summary>Ember/glow objects deleted from the prefab template at build time (test hook).</summary>
    public static int TemplateEffectsRemoved { get; private set; }

    /// <summary>Upward launch velocity applied to the last spawned marker (test hook).</summary>
    public static float LastPopVelY { get; private set; }

    private static GameObject? s_prefabHolder;
    private static GameObject? s_prefabTemplate;

    // =====================================================================
    //  Prefab registration
    // =====================================================================

    /// <summary>
    /// Build (once) and register the marker prefab with a ZNetScene. Called from
    /// the ZNetScene.Awake postfix on every client, so remote instances are
    /// instantiated directly from this prefab via the ZDO prefab hash -- no
    /// spawn-a-tombstone-and-convert hack.
    ///
    /// The template is the vanilla player tombstone cloned under an inactive
    /// holder (so no Awake runs), with the loot/despawn scripts removed and our
    /// components pre-attached.
    /// </summary>
    public static void RegisterPrefab(ZNetScene scene) {
        if (s_prefabTemplate == null) {
            var original = FindTombstonePrefab(scene);
            if (original == null) {
                Plugin.Logger.LogError("MarkerPrefab: no TombStone prefab found to derive the marker from");
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
            Plugin.Logger.LogInfo($"MarkerPrefab: built prefab '{PrefabName}' from '{original.name}'");
        }

        var hash = PrefabName.GetStableHashCode();
        if (!scene.m_namedPrefabs.ContainsKey(hash)) {
            scene.m_prefabs.Add(s_prefabTemplate);
            scene.m_namedPrefabs.Add(hash, s_prefabTemplate);
            Plugin.Logger.LogInfo($"MarkerPrefab: registered prefab '{PrefabName}' with ZNetScene");
        }
    }

    internal static GameObject? FindTombstonePrefab(ZNetScene scene) {
        var byName = scene.GetPrefab("Player_tombstone");
        if (byName != null && byName.GetComponent<TombStone>() != null) return byName;
        foreach (var p in scene.m_prefabs) {
            if (p != null && p.GetComponent<TombStone>() != null) return p;
        }
        return null;
    }

    /// <summary>
    /// Delete ember particle systems and flare/glow children from the prefab
    /// TEMPLATE. The grave effects belong only on a real (unrevivable) grave.
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

    // =====================================================================
    //  Spawn / find
    // =====================================================================

    /// <summary>
    /// Spawn the green marker for a freshly-downed player (owner only) and
    /// cross-link it to the player. Instantiates OUR registered prefab; remote
    /// clients instantiate the same prefab from the replicated ZDO.
    /// </summary>
    public static void Spawn(Player player) {
        var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(PrefabName) : null;
        if (prefab == null) {
            Plugin.Logger.LogError($"MarkerPrefab.Spawn: prefab '{PrefabName}' is not registered");
            return;
        }

        var go = Object.Instantiate(prefab, player.GetCenterPoint(), player.transform.rotation);
        var nview = go.GetComponent<ZNetView>();
        if (nview == null || !nview.IsValid()) {
            Plugin.Logger.LogError("MarkerPrefab.Spawn: marker has no valid ZNetView");
            return;
        }

        var marker = new MarkerState(nview);
        marker.IsMarker = true;                                   // distinguishes from real graves
        marker.LinkedPlayer = player.m_nview.GetZDO().m_uid;
        marker.OwnerPlayerId = player.GetPlayerID();              // stable across rejoin
        marker.OwnerName = player.GetPlayerName();                // world text
        marker.DownedTime = (float)ZNet.instance.GetTimeSeconds();// fallback clock

        var state = new DownedState(player.m_nview);
        state.Marker = nview.GetZDO().m_uid;

        // Vanilla tombstone "drop-in" pop (TombStone.Setup normally does this;
        // our marker has no TombStone script).
        var body = go.GetComponent<Rigidbody>();
        if (body != null) body.linearVelocity = new Vector3(0f, 5f, 0f);
        LastPopVelY = body != null ? body.linearVelocity.y : 0f;

        Plugin.Logger.LogInfo($"Spawned downed marker for {player.GetPlayerName()}, ZDOID {nview.GetZDO().m_uid}");
    }

    /// <summary>
    /// Find the (non-replaced) marker for a stable PlayerID -- the orphan left
    /// behind when a downed player disconnects, matched again on reconnect.
    /// </summary>
    public static GameObject? FindFor(long playerId) {
        if (playerId == 0L) return null;
        foreach (var dm in Object.FindObjectsOfType<DownedMarker>()) {
            var nv = dm.GetComponent<ZNetView>();
            if (nv == null || !nv.IsValid()) continue;
            var marker = new MarkerState(nv);
            // A replaced marker is a lame-duck prop awaiting destruction, not
            // evidence of a downed player (it must not re-trigger the
            // reconnect-death check while it lingers).
            if (marker.ReplacedByGrave) continue;
            if (marker.OwnerPlayerId == playerId) return dm.gameObject;
        }
        return null;
    }
}
