using System.Collections.Generic;
using System.Text;

namespace Automations.Domain;

/// <summary>
/// A blueprint for the assembler: a fixed transformation it applies each tick when
/// its inputs are present and its output has room. Inputs are consumed from the
/// assembler's buffer; outputs are added to it (and shipped downstream by pipes).
/// Immutable, shared catalog data. Item ids are real Valheim item prefab names.
/// </summary>
public sealed class Recipe {
    public readonly string Label;
    public readonly IReadOnlyDictionary<string, int> Inputs;
    public readonly IReadOnlyDictionary<string, int> Outputs;

    public Recipe(string label, Dictionary<string, int> inputs, Dictionary<string, int> outputs) {
        Label = label;
        Inputs = inputs;
        Outputs = outputs;
    }

    public static Recipe Of(string a, int aQty, string b, int bQty, string outItem, int outQty) =>
        new($"{a}+{b} -> {outItem}",
            new Dictionary<string, int> { [a] = aQty, [b] = bQty },
            new Dictionary<string, int> { [outItem] = outQty });

    public string Describe() {
        var sb = new StringBuilder();
        bool first = true;
        foreach (var kv in Inputs) { if (!first) sb.Append('+'); sb.Append(kv.Value).Append(' ').Append(kv.Key); first = false; }
        sb.Append(" -> ");
        first = true;
        foreach (var kv in Outputs) { if (!first) sb.Append('+'); sb.Append(kv.Value).Append(' ').Append(kv.Key); first = false; }
        return sb.ToString();
    }
}

/// <summary>The blueprints the assembler can be set to. Numeric index is the wire format.</summary>
public static class Blueprints {
    public const string PrefabName = "Automations_Assembler";
    public const int Capacity = 20;

    public static readonly IReadOnlyList<Recipe> All = new[] {
        Recipe.Of("Copper", 1, "Tin", 1, "Bronze", 1),
        Recipe.Of("Bronze", 1, "Wood", 1, "BronzeNails", 5),
    };

    private static readonly HashSet<string> InputItems = BuildInputs();
    private static readonly HashSet<string> OutputItems = BuildOutputs();

    private static HashSet<string> BuildInputs() {
        var s = new HashSet<string>();
        foreach (var r in All) foreach (var k in r.Inputs.Keys) s.Add(k);
        return s;
    }

    private static HashSet<string> BuildOutputs() {
        var s = new HashSet<string>();
        foreach (var r in All) foreach (var k in r.Outputs.Keys) s.Add(k);
        return s;
    }

    /// <summary>An item any blueprint consumes (so pipes may deliver it).</summary>
    public static bool IsInput(string item) => InputItems.Contains(item);

    /// <summary>An item some blueprint produces (so it may leave down a pipe).</summary>
    public static bool IsOutput(string item) => OutputItems.Contains(item);

    public static Recipe At(int index) => All[((index % All.Count) + All.Count) % All.Count];
}
