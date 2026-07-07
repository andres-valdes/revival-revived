using System.Collections.Generic;

namespace Automations.Domain;

/// <summary>
/// The assembler-specific ZDO fields: its selected blueprint and its internal item
/// buffer. These live alongside the pipe-layer <see cref="MachineView"/> on the same
/// ZDO (different keys). Unlike a chest or smelter, the assembler has no vanilla
/// storage to borrow, so it keeps its own buffer here. Owner is the sole writer.
/// </summary>
public struct AssemblerView {
    private static readonly int kBlueprint = (MachineView.Prefix + "bp").GetStableHashCode();
    private static readonly int kBuffer = (MachineView.Prefix + "asmbuf").GetStableHashCode();

    private readonly ZDO _z;
    public AssemblerView(ZNetView nview) : this(nview.GetZDO()) { }
    public AssemblerView(ZDO zdo) { _z = zdo; }

    public int Blueprint {
        get => _z.GetInt(kBlueprint);
        set => _z.Set(kBlueprint, value);
    }

    public Recipe Recipe => Blueprints.At(Blueprint);

    public Dictionary<string, int> ReadBuffer() => ItemBuffer.Parse(_z.GetString(kBuffer));
    public void WriteBuffer(Dictionary<string, int> map) => _z.Set(kBuffer, ItemBuffer.Format(map));

    public int Count(string item) => ReadBuffer().TryGetValue(item, out var n) ? n : 0;
    public int FreeSpace(string item) => System.Math.Max(0, Blueprints.Capacity - Count(item));

    public int Add(string item, int qty) {
        if (qty <= 0) return 0;
        var map = ReadBuffer();
        int have = map.TryGetValue(item, out var n) ? n : 0;
        int added = System.Math.Min(System.Math.Max(0, Blueprints.Capacity - have), qty);
        if (added > 0) { map[item] = have + added; WriteBuffer(map); }
        return added;
    }

    public int Remove(string item, int qty) {
        if (qty <= 0) return 0;
        var map = ReadBuffer();
        if (!map.TryGetValue(item, out var have) || have <= 0) return 0;
        int removed = System.Math.Min(have, qty);
        int left = have - removed;
        if (left > 0) map[item] = left; else map.Remove(item);
        WriteBuffer(map);
        return removed;
    }
}
