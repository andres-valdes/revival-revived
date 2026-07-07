using System.Collections.Generic;

namespace Automations.Domain;

/// <summary>
/// Static definition of one machine kind: its identity, the vanilla piece its
/// visuals are cloned from, its buffer capacity, the recipes/raw-items its
/// blueprint selects between, and what item ids it will accept from an upstream
/// pipe. This is pure data -- per-instance state lives on the ZDO
/// (<see cref="MachineView"/>).
/// </summary>
public sealed class MachineDef {
    public readonly MachineKind Kind;
    public readonly string DisplayName;
    /// <summary>Networked prefab name, identical on every client.</summary>
    public readonly string PrefabName;
    /// <summary>Vanilla prefab whose model/collider/piece we clone and curate.</summary>
    public readonly string SourcePrefab;
    /// <summary>Per-item stack cap in the buffer.</summary>
    public readonly int Capacity;

    /// <summary>Processing recipes selectable by blueprint index (empty for pure storage).</summary>
    public readonly IReadOnlyList<Recipe> Recipes;
    /// <summary>Raw items a Stockpile can be set to produce, selectable by blueprint index.</summary>
    public readonly IReadOnlyList<string> RawItems;
    /// <summary>When true, accepts any item from upstream (storage/collector).</summary>
    public readonly bool AcceptsAnything;

    private readonly HashSet<string> _accepts;

    public MachineDef(MachineKind kind, string display, string prefabName, string sourcePrefab,
                      int capacity, IReadOnlyList<Recipe>? recipes = null,
                      IReadOnlyList<string>? rawItems = null, bool acceptsAnything = false) {
        Kind = kind;
        DisplayName = display;
        PrefabName = prefabName;
        SourcePrefab = sourcePrefab;
        Capacity = capacity;
        Recipes = recipes ?? System.Array.Empty<Recipe>();
        RawItems = rawItems ?? System.Array.Empty<string>();
        AcceptsAnything = acceptsAnything;

        // A processor accepts the union of every recipe's inputs, so pipes can
        // deliver regardless of which blueprint is currently selected.
        _accepts = new HashSet<string>();
        foreach (var r in Recipes) {
            foreach (var input in r.Inputs.Keys) _accepts.Add(input);
        }
    }

    /// <summary>Number of blueprint options (raw items for a Stockpile, recipes otherwise).</summary>
    public int BlueprintCount => Kind == MachineKind.Stockpile ? RawItems.Count : Recipes.Count;

    /// <summary>Whether an upstream pipe may deliver this item into this machine.</summary>
    public bool Accepts(string itemId) => AcceptsAnything || _accepts.Contains(itemId);

    /// <summary>The recipe a processor runs at the given blueprint index, or null.</summary>
    public Recipe? RecipeAt(int blueprint) =>
        Recipes.Count == 0 ? null : Recipes[((blueprint % Recipes.Count) + Recipes.Count) % Recipes.Count];

    /// <summary>The raw item a Stockpile produces at the given blueprint index, or null.</summary>
    public string? RawItemAt(int blueprint) =>
        RawItems.Count == 0 ? null : RawItems[((blueprint % RawItems.Count) + RawItems.Count) % RawItems.Count];
}

/// <summary>
/// The registry of every machine kind. Item ids are real Valheim item prefab
/// names, so the buffers move genuine materials (Wood, Coal, CopperOre, Bronze...).
/// </summary>
public static class MachineCatalog {
    public const string Prefix = "Automations_";

    public static readonly MachineDef Stockpile = new(
        MachineKind.Stockpile, "Stockpile", Prefix + "Stockpile", "piece_chest_wood",
        capacity: 40,
        rawItems: new[] { "Wood", "CopperOre", "TinOre" });

    public static readonly MachineDef Chest = new(
        MachineKind.Chest, "Collector Chest", Prefix + "Chest", "piece_chest_wood",
        capacity: 400,
        acceptsAnything: true);

    public static readonly MachineDef Kiln = new(
        MachineKind.Kiln, "Automated Kiln", Prefix + "Kiln", "charcoal_kiln",
        capacity: 25,
        recipes: new[] { Recipe.Of("Wood", 2, "Coal", 1) });

    public static readonly MachineDef Smelter = new(
        MachineKind.Smelter, "Automated Smelter", Prefix + "Smelter", "smelter",
        capacity: 25,
        recipes: new[] {
            Recipe.Of("CopperOre", 1, "Coal", 1, "Copper", 1),
            Recipe.Of("TinOre", 1, "Coal", 1, "Tin", 1),
        });

    public static readonly MachineDef Assembler = new(
        MachineKind.Assembler, "Blueprint Assembler", Prefix + "Assembler", "forge",
        capacity: 25,
        recipes: new[] {
            Recipe.Of("Copper", 1, "Tin", 1, "Bronze", 1),
            Recipe.Of("Bronze", 1, "Wood", 1, "BronzeNails", 5),
        });

    public static readonly IReadOnlyList<MachineDef> All =
        new[] { Stockpile, Chest, Kiln, Smelter, Assembler };

    private static readonly Dictionary<MachineKind, MachineDef> ByKind = BuildKindMap();
    private static readonly Dictionary<string, MachineDef> ByPrefab = BuildPrefabMap();
    private static readonly Dictionary<int, MachineDef> ByPrefabHash = BuildPrefabHashMap();

    private static Dictionary<MachineKind, MachineDef> BuildKindMap() {
        var d = new Dictionary<MachineKind, MachineDef>();
        foreach (var def in All) d[def.Kind] = def;
        return d;
    }

    private static Dictionary<string, MachineDef> BuildPrefabMap() {
        var d = new Dictionary<string, MachineDef>();
        foreach (var def in All) d[def.PrefabName] = def;
        return d;
    }

    private static Dictionary<int, MachineDef> BuildPrefabHashMap() {
        var d = new Dictionary<int, MachineDef>();
        foreach (var def in All) d[def.PrefabName.GetStableHashCode()] = def;
        return d;
    }

    public static MachineDef ForKind(MachineKind kind) => ByKind[kind];

    public static MachineDef? ForPrefab(string prefabName) =>
        ByPrefab.TryGetValue(prefabName, out var def) ? def : null;

    /// <summary>
    /// The machine kind identified by a ZDO's prefab hash. The prefab hash is core
    /// ZDO data (set at creation), so this is authoritative regardless of how the
    /// machine was placed -- Machine.Spawn or the build hammer alike.
    /// </summary>
    public static MachineDef? ForPrefabHash(int prefabHash) =>
        ByPrefabHash.TryGetValue(prefabHash, out var def) ? def : null;
}
