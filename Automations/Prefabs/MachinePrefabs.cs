using System.Collections.Generic;
using Automations.Components;
using Automations.Domain;
using UnityEngine;

namespace Automations.Prefabs;

/// <summary>
/// Builds and registers every machine prefab (once), derived from a vanilla piece
/// so we inherit its model, collider, Piece and WearNTear without shipping any art
/// -- exactly the marker-from-tombstone trick, applied per machine kind. The
/// vanilla functional scripts (Container, Smelter, forge station...) are stripped
/// off the inactive template and our <see cref="Machine"/> + <see cref="MachineInteract"/>
/// baked in, so the piece keeps its look but behaves as an automation node.
///
/// It also lifts the game's station-connection VFX prefab out of an existing
/// StationExtension (for <see cref="PipeRenderer"/>) and adds each machine to the
/// Hammer's build table so they can be placed in normal play.
/// </summary>
public static class MachinePrefabs {
    private static bool s_built;
    private static GameObject? s_holder;

    /// <summary>The dotted station-connection VFX prefab, cloned from vanilla. Used to draw pipes.</summary>
    public static GameObject? ConnectionPrefab { get; private set; }

    /// <summary>Vanilla station scripts stripped off a cloned piece so only our logic runs.</summary>
    private static readonly System.Type[] StripTypes = {
        typeof(Container), typeof(Smelter), typeof(CookingStation), typeof(Fermenter),
        typeof(CraftingStation), typeof(StationExtension), typeof(Switch), typeof(Beehive),
    };

    public static void RegisterAll(ZNetScene scene) {
        if (!s_built) {
            s_holder = new GameObject("Automations_Prefabs");
            s_holder.SetActive(false); // children never Awake here
            Object.DontDestroyOnLoad(s_holder);

            LiftConnectionPrefab(scene);
            foreach (var def in MachineCatalog.All) BuildMachinePrefab(scene, def);

            s_built = true;
        }

        // Registration is per-ZNetScene (a new one exists each world load).
        foreach (var def in MachineCatalog.All) {
            var prefab = FindTemplate(def.PrefabName);
            if (prefab == null) continue;
            int hash = def.PrefabName.GetStableHashCode();
            if (!scene.m_namedPrefabs.ContainsKey(hash)) {
                scene.m_prefabs.Add(prefab);
                scene.m_namedPrefabs.Add(hash, prefab);
            }
        }

        AddToHammer(scene);
    }

    private static readonly Dictionary<string, GameObject> s_templates = new();
    private static GameObject? FindTemplate(string name) =>
        s_templates.TryGetValue(name, out var go) ? go : null;

    private static void BuildMachinePrefab(ZNetScene scene, MachineDef def) {
        var original = scene.GetPrefab(def.SourcePrefab);
        if (original == null) {
            Plugin.Logger.LogError($"MachinePrefabs: source prefab '{def.SourcePrefab}' not found for {def.DisplayName}");
            return;
        }

        var template = Object.Instantiate(original, s_holder!.transform);
        template.name = def.PrefabName;

        // Strip vanilla station behaviour; keep model, collider, Piece, WearNTear, ZNetView.
        foreach (var type in StripTypes) {
            foreach (var comp in template.GetComponentsInChildren(type, includeInactive: true)) {
                if (comp is Component c && c != null) Object.DestroyImmediate(c);
            }
        }

        // Re-brand the piece.
        var piece = template.GetComponent<Piece>();
        if (piece != null) {
            piece.m_name = def.DisplayName;
            piece.m_description = "Automation machine. Pipe items in and out; set a blueprint.";
        }

        // Bake in our behaviour.
        template.AddComponent<Machine>();
        template.AddComponent<MachineInteract>();

        s_templates[def.PrefabName] = template;
        Plugin.Logger.LogInfo($"MachinePrefabs: built '{def.PrefabName}' from '{def.SourcePrefab}'");
    }

    /// <summary>Clone the game's own station-connection VFX from any StationExtension prefab.</summary>
    private static void LiftConnectionPrefab(ZNetScene scene) {
        if (ConnectionPrefab != null) return;
        foreach (var p in scene.m_prefabs) {
            if (p == null) continue;
            var ext = p.GetComponentInChildren<StationExtension>(includeInactive: true);
            if (ext != null && ext.m_connectionPrefab != null) {
                var clone = Object.Instantiate(ext.m_connectionPrefab, s_holder!.transform);
                clone.name = "Automations_PipeConnection";
                clone.SetActive(false);
                ConnectionPrefab = clone;
                Plugin.Logger.LogInfo($"MachinePrefabs: lifted connection VFX from '{p.name}'");
                return;
            }
        }
        Plugin.Logger.LogWarning("MachinePrefabs: no StationExtension found to lift a connection VFX from");
    }

    /// <summary>Add every machine piece to the Hammer's build table so players can place them.</summary>
    private static void AddToHammer(ZNetScene scene) {
        var hammer = scene.GetPrefab("Hammer");
        var table = hammer != null ? hammer.GetComponent<ItemDrop>()?.m_itemData?.m_shared?.m_buildPieces : null;
        if (table == null) return;

        foreach (var def in MachineCatalog.All) {
            var prefab = FindTemplate(def.PrefabName);
            if (prefab == null) continue;
            if (!table.m_pieces.Contains(prefab)) table.m_pieces.Add(prefab);
        }
    }
}
