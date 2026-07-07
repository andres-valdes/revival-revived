using System.Collections.Generic;
using System.Text;

namespace Automations.Domain;

/// <summary>
/// A machine's internal contents: a small item->count map. Encoded as a compact,
/// human-readable string ("Wood:3,Coal:2") so it round-trips through a single
/// replicated ZDO string field and is trivially inspectable in logs. The encoding
/// is the wire format; keep it stable.
/// </summary>
public static class ItemBuffer {
    /// <summary>Parse a buffer string into an ordered item->count map (empty on blank).</summary>
    public static Dictionary<string, int> Parse(string? raw) {
        var result = new Dictionary<string, int>();
        if (string.IsNullOrEmpty(raw)) return result;
        foreach (var part in raw!.Split(',')) {
            if (part.Length == 0) continue;
            int colon = part.LastIndexOf(':');
            if (colon <= 0) continue;
            var item = part.Substring(0, colon);
            if (int.TryParse(part.Substring(colon + 1), out var n) && n > 0) {
                result[item] = n;
            }
        }
        return result;
    }

    /// <summary>Encode an item->count map back to the buffer string (zero/negative counts dropped).</summary>
    public static string Format(Dictionary<string, int> map) {
        var sb = new StringBuilder();
        foreach (var kv in map) {
            if (kv.Value <= 0) continue;
            if (sb.Length > 0) sb.Append(',');
            sb.Append(kv.Key).Append(':').Append(kv.Value);
        }
        return sb.ToString();
    }

    /// <summary>Total number of items held (sum of all counts).</summary>
    public static int TotalCount(Dictionary<string, int> map) {
        int total = 0;
        foreach (var v in map.Values) total += v;
        return total;
    }
}
