using System;
using System.Collections.Generic;

namespace FuriousAntiCheat
{
    internal static class ModEnforcer
    {
        private struct PendingHandshake
        {
            public ZNetPeer Peer;
            public DateTime SentAt;
            public int Attempts;
        }

        private struct PendingRejection
        {
            public ZNetPeer Peer;
            public DateTime ReasonSentAt;
            public DateTime ErrorSentAt;
            public bool ErrorSent;
        }

        private static readonly Dictionary<long, PendingHandshake> PendingHandshakes
            = new Dictionary<long, PendingHandshake>();
        private static readonly Dictionary<long, PendingRejection> PendingRejections
            = new Dictionary<long, PendingRejection>();

        private static readonly TimeSpan RejectionAckTimeout = TimeSpan.FromSeconds(1.5);
        private static readonly TimeSpan DisconnectFlushDelay = TimeSpan.FromSeconds(0.35);

        public static void RegisterPending(ZNetPeer peer)
        {
            if (peer == null) return;
            PendingHandshakes[peer.m_uid] = new PendingHandshake
            {
                Peer = peer,
                SentAt = DateTime.UtcNow,
                Attempts = 0,
            };
        }

        public static void TickTimeouts()
        {
            TickPendingRejections();
            if (PendingHandshakes.Count == 0) return;

            var timeout = TimeSpan.FromSeconds(Plugin.KickTimeoutSeconds.Value);
            var now = DateTime.UtcNow;
            var expired = new List<long>();
            foreach (var item in PendingHandshakes)
                if (now - item.Value.SentAt > timeout) expired.Add(item.Key);

            foreach (var uid in expired)
            {
                if (!PendingHandshakes.TryGetValue(uid, out var pending)) continue;

                if (pending.Attempts < Plugin.HandshakeRetries.Value)
                {
                    pending.Attempts++;
                    pending.SentAt = now;
                    PendingHandshakes[uid] = pending;
                    try
                    {
                        Plugin.Log.LogWarning(
                            $"No mod-list reply from {Describe(pending.Peer)} after " +
                            $"{Plugin.KickTimeoutSeconds.Value:0.#}s; retry " +
                            $"{pending.Attempts}/{Plugin.HandshakeRetries.Value}.");
                    }
                    catch { }

                    try { pending.Peer.m_rpc?.Invoke("FuriousAC_Request"); }
                    catch (Exception ex)
                    {
                        PendingHandshakes.Remove(uid);
                        Reject(pending.Peer, $"No se pudo repetir la validación de mods: {ex.Message}");
                    }
                    continue;
                }

                PendingHandshakes.Remove(uid);
                Reject(
                    pending.Peer,
                    $"El cliente no respondió a la validación en {Plugin.KickTimeoutSeconds.Value:0.#} segundos. " +
                    "FuriousAntiCheat puede estar ausente o ser incompatible.");
            }
        }

        public static void OnReceiveClientList(
            ZNetPeer peer,
            Dictionary<string, ModFingerprint> clientMods)
        {
            if (peer == null) return;
            PendingHandshakes.Remove(peer.m_uid);

            var problems = Validate(clientMods);
            if (problems.Count == 0)
            {
                if (Plugin.LogAcceptedConnections.Value)
                {
                    try { Plugin.Log.LogInfo($"Peer {Describe(peer)} mod set OK ({clientMods.Count} mods)."); }
                    catch { }
                }
                return;
            }

            var summary = FormatProblems(problems);
            if (IsAuditOnly)
            {
                try
                {
                    Plugin.Log.LogWarning(
                        $"Audit-only: would reject peer {Describe(peer)} - " +
                        $"{problems.Count} mod problem(s): {summary}");
                }
                catch { }
                return;
            }

            try
            {
                Plugin.Log.LogWarning(
                    $"Kicking peer {Describe(peer)} - {problems.Count} mod problem(s): {summary}");
            }
            catch { }
            BeginRejection(peer, summary);
        }

        public static void Reject(ZNetPeer peer, string reason)
        {
            if (peer == null) return;
            PendingHandshakes.Remove(peer.m_uid);
            if (IsAuditOnly)
            {
                try { Plugin.Log.LogWarning($"Audit-only: would reject peer {Describe(peer)} - {reason}"); }
                catch { }
                return;
            }

            try { Plugin.Log.LogWarning($"Kicking peer {Describe(peer)} - {reason}"); }
            catch { }
            BeginRejection(peer, reason);
        }

        public static void AcknowledgeRejection(ZNetPeer peer)
        {
            try
            {
                if (peer == null) return;
                if (!PendingRejections.TryGetValue(peer.m_uid, out var pending)) return;
                SendKickStatus(ref pending);
                PendingRejections[peer.m_uid] = pending;
            }
            catch (Exception ex)
            {
                Plugin.SafeLogError($"Rejection acknowledgement failed safely: {ex}");
            }
        }

        public static void ClearPending(long uid)
        {
            try { PendingHandshakes.Remove(uid); } catch { }
        }

        private static void BeginRejection(ZNetPeer peer, string reason)
        {
            try
            {
                if (peer == null) return;
                var safeReason = SanitizeReason(reason);
                PendingRejections[peer.m_uid] = new PendingRejection
                {
                    Peer = peer,
                    ReasonSentAt = DateTime.UtcNow,
                    ErrorSentAt = DateTime.MinValue,
                    ErrorSent = false,
                };

                try
                {
                    var package = new ZPackage();
                    package.Write(safeReason);
                    peer.m_rpc?.Invoke("FuriousAC_Reject", package);
                }
                catch (Exception ex)
                {
                    Plugin.SafeLogError($"Could not send the detailed rejection reason; fallback will be used: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                Plugin.SafeLogError($"Could not schedule rejection safely: {ex}");
                SafeDisconnect(peer);
            }
        }

        private static void TickPendingRejections()
        {
            if (PendingRejections.Count == 0) return;
            var now = DateTime.UtcNow;
            var ids = new List<long>(PendingRejections.Keys);

            foreach (var uid in ids)
            {
                if (!PendingRejections.TryGetValue(uid, out var pending)) continue;
                try
                {
                    if (!pending.ErrorSent && now - pending.ReasonSentAt >= RejectionAckTimeout)
                    {
                        SendKickStatus(ref pending);
                        PendingRejections[uid] = pending;
                    }

                    if (pending.ErrorSent && now - pending.ErrorSentAt >= DisconnectFlushDelay)
                    {
                        PendingRejections.Remove(uid);
                        SafeDisconnect(pending.Peer);
                    }
                }
                catch (Exception ex)
                {
                    PendingRejections.Remove(uid);
                    Plugin.SafeLogError($"Deferred rejection failed safely: {ex}");
                    SafeDisconnect(pending.Peer);
                }
            }
        }

        private static void SendKickStatus(ref PendingRejection pending)
        {
            if (pending.ErrorSent) return;
            try
            {
                pending.Peer?.m_rpc?.Invoke(
                    "Error",
                    new object[] { (int)ZNet.ConnectionStatus.ErrorKicked });
            }
            catch (Exception ex)
            {
                Plugin.SafeLogError($"Could not send ErrorKicked status safely: {ex.Message}");
            }
            finally
            {
                pending.ErrorSent = true;
                pending.ErrorSentAt = DateTime.UtcNow;
            }
        }

        private static List<string> Validate(Dictionary<string, ModFingerprint> client)
        {
            var problems = new List<string>();
            if (!ModManifest.IsValid)
            {
                problems.Add("El manifiesto de mods del servidor no es válido. Contacta a un administrador.");
                return problems;
            }

            foreach (var expected in ModManifest.Required.Values)
            {
                if (!client.TryGetValue(expected.Guid, out var actual))
                    problems.Add($"Falta el mod obligatorio {expected.Guid} v{expected.Version}.");
                else
                    CompareExact(expected, actual, problems);
            }

            foreach (var expected in ModManifest.Optional.Values)
                if (client.TryGetValue(expected.Guid, out var actual))
                    CompareExact(expected, actual, problems);

            foreach (var actual in client.Values)
            {
                if (ModManifest.Required.ContainsKey(actual.Guid)) continue;
                if (ModManifest.Optional.ContainsKey(actual.Guid)) continue;
                problems.Add($"Mod no autorizado: {actual.Guid} v{actual.Version}.");
            }
            return problems;
        }

        private static void CompareExact(
            ModFingerprint expected,
            ModFingerprint actual,
            List<string> problems)
        {
            if (!string.Equals(expected.Version, actual.Version, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add(
                    $"Versión incorrecta de {expected.Guid}. " +
                    $"Instalada: {actual.Version}. Requerida: {expected.Version}.");
            }
            else if (!string.Equals(expected.Sha256, actual.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add(
                    $"El archivo de {expected.Guid} v{expected.Version} no coincide con el requerido por el servidor.");
            }
        }

        private static string FormatProblems(List<string> problems)
        {
            var max = Math.Max(1, Plugin.MaxDisplayedProblems.Value);
            var shown = problems.Count > max ? problems.GetRange(0, max) : problems;
            var result = string.Join("\n", shown);
            if (problems.Count > max)
                result += $"\n... y {problems.Count - max} problema(s) más.";
            return result;
        }

        private static string SanitizeReason(string value)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(value)) return "Conexión rechazada por FuriousAntiCheat.";
                var chars = new List<char>(Math.Min(value.Length, 800));
                foreach (var c in value)
                {
                    if (chars.Count >= 800) break;
                    if (c == '<' || c == '>') continue;
                    if (char.IsControl(c) && c != '\n') continue;
                    chars.Add(c);
                }
                var result = new string(chars.ToArray()).Trim();
                return result.Length == 0 ? "Conexión rechazada por FuriousAntiCheat." : result;
            }
            catch
            {
                return "Conexión rechazada por FuriousAntiCheat.";
            }
        }

        private static void SafeDisconnect(ZNetPeer peer)
        {
            try
            {
                if (peer != null) ZNet.instance?.Disconnect(peer);
            }
            catch (Exception ex)
            {
                Plugin.SafeLogError($"Peer disconnect failed safely: {ex.Message}");
            }
        }

        private static bool IsAuditOnly =>
            string.Equals(Plugin.EnforcementMode.Value, "AuditOnly", StringComparison.OrdinalIgnoreCase);

        private static string Describe(ZNetPeer peer) =>
            peer == null ? "<null>" : $"{peer.m_uid}/{peer.m_playerName ?? "<noname>"}";
    }
}
