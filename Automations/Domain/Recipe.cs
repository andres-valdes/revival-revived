using System.Collections.Generic;
using System.Text;

namespace Automations.Domain;

/// <summary>
/// A blueprint: a fixed transformation a processing machine applies each tick when
/// its inputs are present and its output has room. Inputs are consumed from the
/// machine's buffer; outputs are added to it (and shipped downstream by pipes).
/// Immutable and shared -- recipes are catalog data, never per-instance state.
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

    /// <summary>Convenience: single input -> single output recipe.</summary>
    public static Recipe Of(string inItem, int inQty, string outItem, int outQty) =>
        new($"{inQty}x{inItem} -> {outQty}x{outItem}",
            new Dictionary<string, int> { [inItem] = inQty },
            new Dictionary<string, int> { [outItem] = outQty });

    /// <summary>Convenience: two inputs -> single output (alloys, assemblies).</summary>
    public static Recipe Of(string a, int aQty, string b, int bQty, string outItem, int outQty) =>
        new($"{aQty}x{a}+{bQty}x{b} -> {outQty}x{outItem}",
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
