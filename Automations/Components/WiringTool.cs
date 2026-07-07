using System.Collections.Generic;
using Automations.Domain;

namespace Automations.Components;

/// <summary>
/// The connector: creates and removes directional pipes between machines. Pipe
/// state itself is authoritative on the SOURCE machine's ZDO
/// (<see cref="MachineView.Outputs"/>); this class only holds the local, per-player
/// selection intent (which machine you picked first) and applies edits by claiming
/// the source and writing its ZDO. Selection is deliberately NOT networked -- it is
/// UI, like a cursor.
/// </summary>
public static class WiringTool {
    private static Machine? s_source;

    /// <summary>The machine currently picked as a pipe's start, or null.</summary>
    public static Machine? PendingSource => (s_source != null && s_source) ? s_source : null;

    /// <summary>Clear any pending selection (e.g. on tool put away).</summary>
    public static void Clear() => s_source = null;

    /// <summary>
    /// One connector click on a machine. The first click picks the source; the
    /// second, on a different machine, lays the pipe. Returns a short status line
    /// for player feedback.
    /// </summary>
    public static string Click(Machine m) {
        if (m == null || !m.ValidView) return "";
        var pending = PendingSource;
        if (pending == null || pending == m) {
            s_source = m;
            return $"Pipe start: {m.DisplayName}";
        }
        s_source = null;
        return Link(pending, m)
            ? $"Piped {pending.DisplayName} -> {m.DisplayName}"
            : "Cannot pipe those machines";
    }

    /// <summary>Create a directional pipe source -> dst (idempotent). Owner-claims the source.</summary>
    public static bool Link(Machine source, Machine dst) {
        if (source == null || dst == null || source == dst) return false;
        if (!source.ValidView || !dst.ValidView) return false;
        source.Claim();
        var view = source.View;
        view.AddOutput(dst.ZdoId);
        Plugin.Logger.LogInfo($"Wired {source.DisplayName}({source.ZdoId}) -> {dst.DisplayName}({dst.ZdoId})");
        return true;
    }

    /// <summary>Remove the pipe source -> dst, if present.</summary>
    public static bool Unlink(Machine source, Machine dst) {
        if (source == null || dst == null || !source.ValidView || !dst.ValidView) return false;
        source.Claim();
        var view = source.View;
        if (!view.HasOutput(dst.ZdoId)) return false;
        view.RemoveOutput(dst.ZdoId);
        return true;
    }

    /// <summary>Remove every pipe leaving a machine.</summary>
    public static void ClearOutputs(Machine source) {
        if (source == null || !source.ValidView) return;
        source.Claim();
        var view = source.View;
        view.Outputs = new List<ZDOID>();
    }
}
