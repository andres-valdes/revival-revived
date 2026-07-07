using System.Collections.Generic;
using System.Text;

namespace Automations.Domain;

/// <summary>
/// The fields on a machine's ZDO, as one typed view -- the single description of
/// that ZDO's shape and the whole authoritative state of a machine. Everything a
/// machine "is" at runtime (its kind, selected blueprint, buffered items, and the
/// pipes leaving it) lives here and replicates for free; the MonoBehaviour
/// (<see cref="Components.Machine"/>) only ticks it.
///
/// The owner is the sole writer (Valheim ZDO rule): mutators assume the caller has
/// already checked <c>nview.IsOwner()</c>. Reads are valid on any peer.
/// </summary>
public struct MachineView {
    private static readonly int kBlueprint = (MachineCatalog.Prefix + "blueprint").GetStableHashCode();
    private static readonly int kBuffer = (MachineCatalog.Prefix + "buffer").GetStableHashCode();
    private static readonly int kOutputs = (MachineCatalog.Prefix + "outputs").GetStableHashCode();
    private static readonly int kTag = (MachineCatalog.Prefix + "tag").GetStableHashCode();

    private readonly ZDO _z;
    public MachineView(ZNetView nview) : this(nview.GetZDO()) { }
    public MachineView(ZDO zdo) { _z = zdo; }

    public bool Valid => _z != null;

    // ------------------------------------------------------------------ identity
    /// <summary>
    /// This machine's definition, derived from its prefab hash -- authoritative and
    /// available immediately (no owner write required), so it is correct even for a
    /// machine placed with the build hammer. Falls back to Stockpile if, somehow,
    /// the prefab is unknown.
    /// </summary>
    public MachineDef Def => MachineCatalog.ForPrefabHash(_z.GetPrefab()) ?? MachineCatalog.Stockpile;

    public MachineKind Kind => Def.Kind;

    /// <summary>Selected blueprint index (recipe for a processor, raw item for a Stockpile).</summary>
    public int Blueprint {
        get => _z.GetInt(kBlueprint);
        set => _z.Set(kBlueprint, value);
    }

    /// <summary>Free-form label -- used by tests to find a specific machine (e.g. the collector).</summary>
    public string Tag {
        get => _z.GetString(kTag);
        set => _z.Set(kTag, value);
    }

    // -------------------------------------------------------------------- buffer
    public Dictionary<string, int> ReadBuffer() => ItemBuffer.Parse(_z.GetString(kBuffer));
    public void WriteBuffer(Dictionary<string, int> map) => _z.Set(kBuffer, ItemBuffer.Format(map));

    public int Count(string item) => ReadBuffer().TryGetValue(item, out var n) ? n : 0;
    public int TotalCount() => ItemBuffer.TotalCount(ReadBuffer());

    /// <summary>Room left for a given item under the def's per-item capacity.</summary>
    public int FreeSpace(string item, int capacity) => System.Math.Max(0, capacity - Count(item));

    /// <summary>Owner-only: add items, clamped to capacity. Returns the amount actually added.</summary>
    public int Add(string item, int qty, int capacity) {
        if (qty <= 0) return 0;
        var map = ReadBuffer();
        int have = map.TryGetValue(item, out var n) ? n : 0;
        int room = System.Math.Max(0, capacity - have);
        int added = System.Math.Min(room, qty);
        if (added > 0) { map[item] = have + added; WriteBuffer(map); }
        return added;
    }

    /// <summary>Owner-only: remove items. Returns the amount actually removed.</summary>
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

    // ------------------------------------------------------------- pipes (outputs)
    /// <summary>The downstream machines this one feeds (directional pipes: this -> each).</summary>
    public List<ZDOID> Outputs {
        get => DecodeIds(_z.GetString(kOutputs));
        set => _z.Set(kOutputs, EncodeIds(value));
    }

    public bool HasOutput(ZDOID target) {
        foreach (var id in Outputs) if (id == target) return true;
        return false;
    }

    /// <summary>Owner-only: add a pipe to <paramref name="target"/> (idempotent).</summary>
    public void AddOutput(ZDOID target) {
        var list = Outputs;
        foreach (var id in list) if (id == target) return;
        list.Add(target);
        Outputs = list;
    }

    /// <summary>Owner-only: remove a pipe to <paramref name="target"/>.</summary>
    public void RemoveOutput(ZDOID target) {
        var list = Outputs;
        if (list.RemoveAll(id => id == target) > 0) Outputs = list;
    }

    private static string EncodeIds(List<ZDOID> ids) {
        var sb = new StringBuilder();
        foreach (var id in ids) {
            if (id == ZDOID.None) continue;
            if (sb.Length > 0) sb.Append(';');
            sb.Append(id.UserID).Append(':').Append(id.ID);
        }
        return sb.ToString();
    }

    private static List<ZDOID> DecodeIds(string? raw) {
        var list = new List<ZDOID>();
        if (string.IsNullOrEmpty(raw)) return list;
        foreach (var part in raw!.Split(';')) {
            if (part.Length == 0) continue;
            int colon = part.IndexOf(':');
            if (colon <= 0) continue;
            if (long.TryParse(part.Substring(0, colon), out var user)
                && uint.TryParse(part.Substring(colon + 1), out var id)) {
                list.Add(new ZDOID(user, id));
            }
        }
        return list;
    }
}
