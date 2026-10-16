using HarmonyLib;
using System;
using System.Collections.Generic;

namespace FuriousAntiCheat
{
    [HarmonyPatch(typeof(ZNet), "OnNewConnection")]
    internal static class ZNet_OnNewConnection_Patch
    {
        private static void Postfix(ZNetPeer peer)
        {
            try
            {
                if (!Plugin.ShouldRun || peer == null || peer.m_rpc == null) return;
                peer.m_rpc.Register("FuriousAC_Request", new ZRpc.RpcMethod.Method(RpcHandlers.OnRequest));
                peer.m_rpc.Register<ZPackage>("FuriousAC_Reply",
                    new Action<ZRpc, ZPackage>(RpcHandlers.OnReply));
                peer.m_rpc.Register<ZPackage>("FuriousAC_Reject",
                    new Action<ZRpc, ZPackage>(RpcHandlers.OnReject));
                peer.m_rpc.Register("FuriousAC_RejectAck",
                    new ZRpc.RpcMethod.Method(RpcHandlers.OnRejectAck));
            }
            catch (Exception ex)
            {
                Plugin.SafeLogError($"Failed to register FuriousAntiCheat RPCs safely: {ex}");
            }
        }
    }

    [HarmonyPatch(typeof(ZNet), "RPC_PeerInfo")]
    internal static class ZNet_RPC_PeerInfo_Patch
    {
        private static void Postfix(ZNet __instance, ZRpc rpc)
        {
            ZNetPeer peer = null;
            try
            {
                if (!Plugin.ShouldRun) return;
                if (__instance == null || !__instance.IsServer()) return;

                peer = RpcHandlers.FindPeer(rpc);
                if (peer == null) return;

                // These calls are cache checks after startup and perform no disk reads when built.
                ServerModSet.BuildOnce();
                ModManifest.BuildOnce();
                try
                {
                    Plugin.Log.LogInfo(
                        $"Starting mod validation for peer {peer.m_uid}/{peer.m_playerName ?? "<noname>"}: " +
                        $"server manifest has {ModManifest.Required.Count} required and " +
                        $"{ModManifest.Optional.Count} optional mod(s).");
                }
                catch { /* Logging is non-critical. */ }

                ModEnforcer.RegisterPending(peer);
                rpc.Invoke("FuriousAC_Request");
            }
            catch (Exception ex)
            {
                Plugin.SafeLogError($"Peer validation startup failed safely: {ex}");
                if (peer != null)
                {
                    try { ModEnforcer.ClearPending(peer.m_uid); } catch { }
                    RpcHandlers.SafeDisconnect(peer);
                }
            }
        }
    }

    [HarmonyPatch(typeof(ZNet), "Update")]
    internal static class ZNet_Update_Patch
    {
        private static bool _disabledAfterFailure;

        private static void Postfix(ZNet __instance)
        {
            if (_disabledAfterFailure) return;
            try
            {
                if (!Plugin.ShouldRun) return;
                if (__instance == null || !__instance.IsServer()) return;
                ModEnforcer.TickTimeouts();
            }
            catch (Exception ex)
            {
                _disabledAfterFailure = true;
                Plugin.SafeLogError(
                    $"Timeout processing failed and was disabled to protect ZNet's main loop: {ex}");
            }
        }
    }

    internal static class RpcHandlers
    {
        public static void OnRequest(ZRpc rpc)
        {
            try
            {
                if (!Plugin.ShouldRun || rpc == null) return;
                ServerModSet.BuildOnce();
                var pkg = new ZPackage();
                ServerModSet.WriteTo(pkg);
                rpc.Invoke("FuriousAC_Reply", pkg);
            }
            catch (Exception ex)
            {
                Plugin.SafeLogError($"OnRequest reply failed safely: {ex}");
            }
        }

        public static void OnReply(ZRpc rpc, ZPackage pkg)
        {
            ZNetPeer peer = null;
            try
            {
                if (!Plugin.ShouldRun) return;
                if (ZNet.instance == null || !ZNet.instance.IsServer()) return;

                peer = FindPeer(rpc);
                if (peer == null) return;

                Dictionary<string, ModFingerprint> mods;
                try
                {
                    mods = ServerModSet.ReadFrom(pkg);
                }
                catch (Exception ex)
                {
                    Plugin.SafeLogError(
                        $"Failed to deserialize client mod list from {peer.m_uid}: {ex.Message}");
                    ModEnforcer.Reject(peer,
                        "incompatible or malformed mod-list handshake. Update FuriousAntiCheat to the server's version.");
                    return;
                }

                try
                {
                    Plugin.Log.LogInfo(
                        $"Received mod list from peer {peer.m_uid}/{peer.m_playerName ?? "<noname>"}: " +
                        $"client reported {mods.Count} detected mod(s); validating now.");
                }
                catch { /* Logging is non-critical. */ }

                ModEnforcer.OnReceiveClientList(peer, mods);
            }
            catch (Exception ex)
            {
                Plugin.SafeLogError($"Client mod validation failed safely: {ex}");
                if (peer != null)
                {
                    try { ModEnforcer.ClearPending(peer.m_uid); } catch { }
                    SafeDisconnect(peer);
                }
            }
        }

        public static void OnReject(ZRpc rpc, ZPackage pkg)
        {
            try
            {
                if (!Plugin.ShouldRun || pkg == null || rpc == null) return;
                if (ZNet.instance != null && ZNet.instance.IsServer()) return;

                var reason = pkg.ReadString();
                RejectionDisplay.SetReason(reason);
                rpc.Invoke("FuriousAC_RejectAck");
            }
            catch (Exception ex)
            {
                Plugin.SafeLogError($"Detailed rejection reception failed safely: {ex}");
            }
        }

        public static void OnRejectAck(ZRpc rpc)
        {
            try
            {
                if (!Plugin.ShouldRun || rpc == null) return;
                if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
                var peer = FindPeer(rpc);
                if (peer != null) ModEnforcer.AcknowledgeRejection(peer);
            }
            catch (Exception ex)
            {
                Plugin.SafeLogError($"Detailed rejection acknowledgement failed safely: {ex}");
            }
        }

        public static ZNetPeer FindPeer(ZRpc rpc)
        {
            if (rpc == null || ZNet.instance == null) return null;
            foreach (var peer in ZNet.instance.GetPeers())
                if (peer != null && peer.m_rpc == rpc) return peer;
            return null;
        }

        public static void SafeDisconnect(ZNetPeer peer)
        {
            if (peer == null) return;
            try { peer.m_rpc?.Invoke("Disconnect"); } catch { }
            try { ZNet.instance?.Disconnect(peer); } catch { }
        }
    }
}
