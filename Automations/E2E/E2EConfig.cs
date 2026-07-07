using System;

namespace Automations.E2E;

/// <summary>
/// Central place for E2E configuration read from environment variables.
///
///   AUTO_E2E=1                 enable the harness at all
///   AUTO_E2E_ROLE=host|client  multiplayer role (unset => single-process mode)
///   AUTO_E2E_SCENARIO=...       "factory" (default, single-process) | "flow" | "wiring"
///   AUTO_E2E_PORT=2456          TCP port for the CustomSocket backend
///   AUTO_E2E_WORLD=auto_mp      world name (host creates it)
///   AUTO_E2E_RESULTS=path       where to write the result file
///   AUTO_E2E_LOG=path           per-process harness log
///   AUTO_E2E_LATENCY=ms         artificial one-way latency on the socket backend
/// </summary>
public static class E2EConfig {
    public static bool Enabled => Environment.GetEnvironmentVariable("AUTO_E2E") == "1";

    public static string Role => (Environment.GetEnvironmentVariable("AUTO_E2E_ROLE") ?? "").ToLowerInvariant();
    public static bool MultiplayerMode => Role == "host" || Role == "client";
    public static bool IsHost => Role == "host";
    public static bool IsClient => Role == "client";

    /// <summary>Scenario: "factory" (single-process end-to-end chain), "flow" (cross-client material flow), "wiring" (cross-client pipe replication).</summary>
    public static string Scenario => (Environment.GetEnvironmentVariable("AUTO_E2E_SCENARIO") ?? "factory").ToLowerInvariant();
    public static bool IsFlowScenario => Scenario == "flow";
    public static bool IsWiringScenario => Scenario == "wiring";

    /// <summary>Manual play mode: auto-host/auto-join over CustomSocket, then hand control to the human.</summary>
    public static bool Manual => Environment.GetEnvironmentVariable("AUTO_E2E_MANUAL") == "1";

    public static int Port {
        get {
            var s = Environment.GetEnvironmentVariable("AUTO_E2E_PORT");
            return int.TryParse(s, out var p) ? p : 2456;
        }
    }

    public static string WorldName =>
        Environment.GetEnvironmentVariable("AUTO_E2E_WORLD") ?? "auto_mp";

    public static string ServerHost =>
        Environment.GetEnvironmentVariable("AUTO_E2E_HOST") ?? "127.0.0.1";

    /// <summary>Artificial one-way latency (ms) injected into the CustomSocket backend.</summary>
    public static int LatencyMs {
        get {
            var s = Environment.GetEnvironmentVariable("AUTO_E2E_LATENCY");
            return int.TryParse(s, out var ms) && ms > 0 ? ms : 0;
        }
    }
}
