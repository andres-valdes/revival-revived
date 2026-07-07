using System.Collections.Generic;
using System.Text;

namespace Automations.Domain;

/// <summary>
/// The pipe-layer state stored on a machine's ZDO -- the fields Automations adds on
/// top of whatever vanilla piece it is attached to. This is just the graph: the
/// directional pipes leaving this machine, a discovery tag, and a small "capture"
/// buffer used by ports whose underlying vanilla component spits items out as
/// world drops (the smelter), holding them until a pipe ships them.
///
/// The machine's REAL contents (a chest's inventory, a smelter's ore/fuel queue)
/// live on the vanilla component and are reached through its <see cref="IMachinePort"/>,
/// not here. The owner is the sole writer.
/// </summary>
public struct MachineView {
    public const string Prefix = "Automations_";

    private static readonly int kOutputs = (Prefix + "outputs").GetStableHashCode();
    private static readonly int kTag = (Prefix + "tag").GetStableHashCode();
    private static readonly int kCapture = (Prefix + "capture").GetStableHashCode();

    private readonly ZDO _z;
    public MachineView(ZNetView nview) : this(nview.GetZDO()) { }
    public MachineView(ZDO zdo) { _z = zdo; }

    public bool Valid => _z != null;

    /// <summary>Free-form label -- used by the E2E harness to find a specific machine.</summary>
    public string Tag {
        get => _z.GetString(kTag);
        set => _z.Set(kTag, value);
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

    // ----------------------------------------------------------- capture buffer
    /// <summary>Items produced by the vanilla component and held for shipment (owner-written).</summary>
    public Dictionary<string, int> ReadCapture() => ItemBuffer.Parse(_z.GetString(kCapture));
    public void WriteCapture(Dictionary<string, int> map) => _z.Set(kCapture, ItemBuffer.Format(map));

    // ------------------------------------------------------------------ encoding
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
