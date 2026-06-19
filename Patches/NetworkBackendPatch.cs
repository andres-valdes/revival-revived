using System;
using HarmonyLib;

namespace RevivalRevived.Patches;

/// <summary>
/// E2E-only: enables Valheim's dormant raw-socket ("CustomSocket") network
/// backend so two client processes on one machine can connect peer-to-peer over
/// TCP/localhost without distinct Steam identities.
///
/// Vanilla <see cref="ZNet.OpenServer"/> only ever creates a Steam or PlayFab
/// host socket -- the <c>ZSocket2</c> TCP host (<c>StartHost(int)</c>) has no
/// callers. We patch OpenServer so that when the backend is CustomSocket we
/// stand up a ZSocket2 listener; the existing
/// <c>ZNet.CheckForIncommingServerConnections</c> accept loop and the
/// <c>OnlineBackendType.CustomSocket</c> branch of <c>ClientConnect</c> handle
/// the rest. Client-side connect needs no patch -- it already routes to
/// <c>Connect(host, port)</c> for the CustomSocket backend.
///
/// Only active when RR_E2E_ROLE is set (host/client multiplayer harness).
/// </summary>
[HarmonyPatch(typeof(ZNet), "OpenServer")]
static class ZNetOpenServerSocketPatch {
    static bool Prefix(ZNet __instance) {
        if (!E2E.E2EConfig.MultiplayerMode) return true;
        if (ZNet.m_onlineBackend != OnlineBackendType.CustomSocket) return true;
        if (!__instance.IsServer()) return true;

        int port = E2E.E2EConfig.Port;
        var sock = new ZSocket2();
        if (!sock.StartHost(port)) {
            Plugin.Logger.LogError($"E2E: ZSocket2 host failed to bind port {port}");
            return true;
        }
        __instance.m_hostSocket = sock;
        ZNet.m_openServer = true;
        E2E.E2ELog.Write($"E2E: opened CustomSocket (TCP) host on port {port}");
        return false; // skip the vanilla Steam/PlayFab host setup
    }
}

/// <summary>
/// E2E-only: pump the raw-socket client connector. Vanilla <c>ZNet.Update</c>
/// never calls <c>UpdateClientConnector</c> (the CustomSocket client path is
/// dead code), so a connector created by <c>Connect(host, port)</c> is never
/// advanced and the client "connects" forever. We drive it each frame while a
/// connector is pending.
/// </summary>
[HarmonyPatch(typeof(ZNet), "Update")]
static class ZNetUpdatePumpConnectorPatch {
    static void Postfix(ZNet __instance) {
        if (!E2E.E2EConfig.MultiplayerMode) return;
        if (ZNet.m_onlineBackend != OnlineBackendType.CustomSocket) return;
        if (__instance.m_serverConnector != null) {
            __instance.UpdateClientConnector(UnityEngine.Time.deltaTime);
        }
    }
}

/// <summary>
/// E2E-only: the client half of the CustomSocket handshake. Vanilla
/// <see cref="ZNet.SendPeerInfo"/> unconditionally requests a Steam session
/// ticket for the (zero) server SteamID, which is meaningless over a raw socket
/// and can fail the handshake. For the CustomSocket backend we send the same
/// peer-info package minus the ticket; the server's RPC_PeerInfo only reads the
/// ticket on the Steamworks backend, so this is symmetric.
/// </summary>
[HarmonyPatch(typeof(ZNet), "SendPeerInfo")]
static class ZNetSendPeerInfoSocketPatch {
    static bool Prefix(ZNet __instance, ZRpc rpc, string password) {
        if (!E2E.E2EConfig.MultiplayerMode) return true;
        if (ZNet.m_onlineBackend != OnlineBackendType.CustomSocket) return true;
        if (__instance.IsServer()) return true; // only override the client send

        var pkg = new ZPackage();
        pkg.Write(ZNet.GetUID());
        pkg.Write(global::Version.CurrentVersion.ToString());
        pkg.Write(36u);
        pkg.Write(__instance.m_referencePosition);
        pkg.Write(Game.instance.GetPlayerProfile().GetName());
        // Client extra fields: password hash (empty for our test), no Steam ticket.
        pkg.Write("");
        rpc.Invoke("PeerInfo", pkg);
        E2E.E2ELog.Write("E2E[client]: sent CustomSocket PeerInfo (no steam ticket)");
        return false;
    }
}
