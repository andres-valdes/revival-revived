using System;

namespace RevivalRevived.E2E;

/// <summary>
/// Central place for E2E configuration read from environment variables.
///
///   RR_E2E=1                 enable the harness at all
///   RR_E2E_ROLE=host|client  multiplayer role (unset => single-process mode)
///   RR_E2E_PORT=2456         TCP port for the CustomSocket backend
///   RR_E2E_WORLD=e2e_mp      world name (host creates it)
///   RR_E2E_RESULTS=path      where to write the result file
/// </summary>
public static class E2EConfig {
    public static bool Enabled => Environment.GetEnvironmentVariable("RR_E2E") == "1";

    public static string Role => (Environment.GetEnvironmentVariable("RR_E2E_ROLE") ?? "").ToLowerInvariant();
    public static bool MultiplayerMode => Role == "host" || Role == "client";
    public static bool IsHost => Role == "host";
    public static bool IsClient => Role == "client";

    /// <summary>Multiplayer scenario: "revive" (default), "rejoin", or "vanish".</summary>
    public static string Scenario => (Environment.GetEnvironmentVariable("RR_E2E_SCENARIO") ?? "revive").ToLowerInvariant();
    public static bool IsRejoinScenario => Scenario == "rejoin";
    /// <summary>Client downs itself and logs out while the HOST is mid-channel reviving it.</summary>
    public static bool IsVanishScenario => Scenario == "vanish";

    /// <summary>
    /// Manual play mode: use the harness only to auto-host/auto-join over the
    /// CustomSocket backend, then hand control to the human -- no scenario tests,
    /// no hard timeout, no auto-quit.
    /// </summary>
    public static bool Manual => Environment.GetEnvironmentVariable("RR_E2E_MANUAL") == "1";

    public static int Port {
        get {
            var s = Environment.GetEnvironmentVariable("RR_E2E_PORT");
            return int.TryParse(s, out var p) ? p : 2456;
        }
    }

    public static string WorldName =>
        Environment.GetEnvironmentVariable("RR_E2E_WORLD") ?? "e2e_mp";

    public static string ServerHost =>
        Environment.GetEnvironmentVariable("RR_E2E_HOST") ?? "127.0.0.1";
}
