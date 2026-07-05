using System;
using System.IO;

namespace ReviveAllies.E2E;

/// <summary>
/// Reliable per-process logging for the E2E harness. Writes to BepInEx's logger
/// (stdout when console logging is on) and, crucially, appends to a per-role
/// file given by RR_E2E_LOG. The file path is unique per process, so two
/// concurrent instances don't clobber each other's logs the way the shared
/// BepInEx LogOutput.log does.
/// </summary>
public static class E2ELog {
    private static readonly object s_lock = new();
    private static string? s_path;
    private static bool s_init;

    private static string? Path {
        get {
            if (!s_init) {
                s_path = Environment.GetEnvironmentVariable("RR_E2E_LOG");
                if (string.IsNullOrEmpty(s_path)) {
                    var res = Environment.GetEnvironmentVariable("RR_E2E_RESULTS");
                    if (!string.IsNullOrEmpty(res)) s_path = res + ".log";
                }
                s_init = true;
                if (!string.IsNullOrEmpty(s_path)) {
                    try { File.WriteAllText(s_path, $"# E2E log role={E2EConfig.Role}\n"); } catch { }
                }
            }
            return s_path;
        }
    }

    public static void Write(string msg) {
        Plugin.Logger.LogInfo(msg);
        var p = Path;
        if (string.IsNullOrEmpty(p)) return;
        try {
            lock (s_lock) {
                File.AppendAllText(p, msg + "\n");
            }
        } catch { /* best-effort */ }
    }
}
