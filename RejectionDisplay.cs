using HarmonyLib;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace FuriousAntiCheat
{
    internal static class RejectionDisplay
    {
        private static readonly object Sync = new object();
        private static string _reason;
        private static DateTime _receivedAt;
        private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);

        public static void SetReason(string reason)
        {
            try
            {
                var safe = Sanitize(reason);
                lock (Sync)
                {
                    _reason = safe;
                    _receivedAt = DateTime.UtcNow;
                }
            }
            catch (Exception ex)
            {
                Plugin.SafeLogError($"Could not store rejection reason safely: {ex}");
            }
        }

        public static bool TryGetReason(out string reason)
        {
            reason = null;
            try
            {
                lock (Sync)
                {
                    if (string.IsNullOrWhiteSpace(_reason)) return false;
                    if (DateTime.UtcNow - _receivedAt > Lifetime)
                    {
                        _reason = null;
                        return false;
                    }
                    reason = _reason;
                    return true;
                }
            }
            catch
            {
                reason = null;
                return false;
            }
        }

        public static void Clear()
        {
            try
            {
                lock (Sync)
                {
                    _reason = null;
                    _receivedAt = DateTime.MinValue;
                }
            }
            catch { }
        }

        private static string Sanitize(string value)
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
    }

    [HarmonyPatch(typeof(FejdStartup), "ShowConnectError")]
    internal static class FejdStartup_ShowConnectError_Patch
    {
        private static void Postfix(FejdStartup __instance, ZNet.ConnectionStatus statusOverride)
        {
            try
            {
                if (__instance == null) return;
                var effectiveStatus = statusOverride == ZNet.ConnectionStatus.None
                    ? ZNet.GetConnectionStatus()
                    : statusOverride;
                if (effectiveStatus != ZNet.ConnectionStatus.ErrorKicked) return;
                if (!RejectionDisplay.TryGetReason(out var reason)) return;

                var instance = Traverse.Create(__instance);
                var panel = instance.Field("m_connectionFailedPanel").GetValue<GameObject>();
                var errorText = instance.Field("m_connectionFailedError").GetValue();
                panel?.SetActive(true);
                if (errorText != null)
                    Traverse.Create(errorText).Property("text").SetValue(reason);
            }
            catch (Exception ex)
            {
                Plugin.SafeLogError($"Detailed rejection display failed safely: {ex}");
            }
        }
    }

    [HarmonyPatch(typeof(FejdStartup), "OnConnectionFailedOk")]
    internal static class FejdStartup_OnConnectionFailedOk_Patch
    {
        private static void Postfix()
        {
            try { RejectionDisplay.Clear(); }
            catch { }
        }
    }
}
