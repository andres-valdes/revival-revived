using Automations.Components;
using Automations.Domain;
using UnityEngine;

namespace Automations.Prefabs;

/// <summary>
/// Prefab work for Automations. Ordinary machines are real vanilla pieces (chests,
/// smelters, kilns) that the attach patch turns into machines -- nothing to build
/// for those. The one bespoke prefab is the <see cref="Assembler"/>: no vanilla
/// piece auto-crafts, so we derive its look from the forge (shipping no art, the
/// marker-from-tombstone trick), strip the crafting station off, and bake in our
/// components. It is added to the Hammer's build table so it can be placed.
///
/// We also lift the game's station-connection VFX (the scrolling dotted line) to
/// draw pipes with.
/// </summary>
public static class MachinePrefabs {
    private static bool s_built;
    private static GameObject? s_holder;
    private static GameObject? s_assembler;

    /// <summary>The dotted station-connection VFX prefab, cloned from vanilla.</summary>
    public static GameObject? ConnectionPrefab { get; private set; }

    private static readonly System.Type[] StripTypes = {
        typeof(CraftingStation), typeof(StationExtension), typeof(Container),
        typeof(Smelter), typeof(CookingStation), typeof(Fermenter),
    };

    public static void RegisterAll(ZNetScene scene) {
        if (!s_built) {
            s_holder = new GameObject("Automations_Prefabs");
            s_holder.SetActive(false); // children never Awake here
            Object.DontDestroyOnLoad(s_holder);

            LiftConnectionPrefab(scene);
            BuildAssemblerPrefab(scene);
            s_built = true;
        }

        if (s_assembler != null) {
            int hash = Blueprints.PrefabName.GetStableHashCode();
            if (!scene.m_namedPrefabs.ContainsKey(hash)) {
                scene.m_prefabs.Add(s_assembler);
                scene.m_namedPrefabs.Add(hash, s_assembler);
            }
            AddToHammer(scene);
        }
    }

    private static void BuildAssemblerPrefab(ZNetScene scene) {
        var original = scene.GetPrefab("forge");
        if (original == null) {
            Plugin.Logger.LogError("MachinePrefabs: 'forge' not found to derive the assembler from");
            return;
        }
        var template = Object.Instantiate(original, s_holder!.transform);
        template.name = Blueprints.PrefabName;

        foreach (var type in StripTypes) {
            foreach (var comp in template.GetComponentsInChildren(type, includeInactive: true)) {
                if (comp is Component c && c != null) Object.DestroyImmediate(c);
            }
        }

        var piece = template.GetComponent<Piece>();
        if (piece != null) {
            piece.m_name = "Blueprint Assembler";
            piece.m_description = "Automatically crafts a set blueprint from piped-in materials.";
        }

        template.AddComponent<Machine>();
        template.AddComponent<Assembler>();
        template.AddComponent<AssemblerInteract>();

        s_assembler = template;
        Plugin.Logger.LogInfo($"MachinePrefabs: built '{Blueprints.PrefabName}' from 'forge'");
    }

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

    private static void AddToHammer(ZNetScene scene) {
        var hammer = scene.GetPrefab("Hammer");
        var table = hammer != null ? hammer.GetComponent<ItemDrop>()?.m_itemData?.m_shared?.m_buildPieces : null;
        if (table == null || s_assembler == null) return;
        if (!table.m_pieces.Contains(s_assembler)) table.m_pieces.Add(s_assembler);
    }
}
