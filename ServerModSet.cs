using BepInEx.Bootstrap;
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace FuriousAntiCheat
{
    internal sealed class ModFingerprint
    {
        public string Guid { get; }
        public string Version { get; }
        public string Sha256 { get; }

        public ModFingerprint(string guid, string version, string sha256)
        {
            Guid = guid;
            Version = version;
            Sha256 = sha256;
        }
    }

    internal static class ServerModSet
    {
        private static readonly object BuildLock = new object();
        private static bool _isBuilt;

        public const int ProtocolVersion = 2;
        public const int MaxMods = 100;
        private const int MaxGuidLength = 128;
        private const int MaxVersionLength = 64;

        public static Dictionary<string, ModFingerprint> Mods { get; private set; }
            = new Dictionary<string, ModFingerprint>(StringComparer.OrdinalIgnoreCase);

        public static int Count => Mods.Count;

        public static void BuildOnce()
        {
            if (_isBuilt) return;
            lock (BuildLock)
            {
                if (_isBuilt) return;
                var fresh = new Dictionary<string, ModFingerprint>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    foreach (var kv in Chainloader.PluginInfos)
                    {
                        var info = kv.Value;
                        var meta = info?.Metadata;
                        if (meta == null || string.IsNullOrWhiteSpace(meta.GUID)) continue;

                        try
                        {
                            fresh[meta.GUID] = new ModFingerprint(
                                meta.GUID,
                                meta.Version?.ToString() ?? "0.0.0",
                                ComputeSha256(info.Location));
                        }
                        catch (Exception ex)
                        {
                            try { Plugin.Log.LogError($"Could not hash loaded mod '{meta.GUID}' at '{info.Location}': {ex.Message}"); }
                            catch { /* Never propagate a logging failure. */ }
                        }
                    }
                }
                catch (Exception ex)
                {
                    fresh.Clear();
                    try { Plugin.Log.LogError($"Loaded mod enumeration failed safely: {ex}"); }
                    catch { /* Never propagate a logging failure. */ }
                }
                finally
                {
                    Mods = fresh;
                    _isBuilt = true;
                }
                try { Plugin.Log.LogInfo($"Cached SHA-256 fingerprints for {fresh.Count} loaded mod(s)."); }
                catch { /* Never propagate a logging failure. */ }
            }
        }

        public static void WriteTo(ZPackage pkg)
        {
            pkg.Write(ProtocolVersion);
            pkg.Write(Mods.Count);
            foreach (var mod in Mods.Values)
            {
                pkg.Write(mod.Guid);
                pkg.Write(mod.Version);
                pkg.Write(mod.Sha256);
            }
        }

        public static Dictionary<string, ModFingerprint> ReadFrom(ZPackage pkg)
        {
            var protocol = pkg.ReadInt();
            if (protocol != ProtocolVersion)
                throw new InvalidDataException(
                    $"Unsupported FuriousAntiCheat protocol {protocol}; expected {ProtocolVersion}.");

            var count = pkg.ReadInt();
            if (count < 0 || count > MaxMods)
                throw new InvalidDataException($"Invalid client mod count {count}.");

            var result = new Dictionary<string, ModFingerprint>(count, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < count; i++)
            {
                var guid = pkg.ReadString();
                var version = pkg.ReadString();
                var hash = pkg.ReadString();

                if (string.IsNullOrWhiteSpace(guid) || guid.Length > MaxGuidLength)
                    throw new InvalidDataException("Client mod list contained an invalid GUID.");
                if (version == null || version.Length > MaxVersionLength)
                    throw new InvalidDataException($"Client mod '{guid}' contained an invalid version.");
                if (!IsSha256(hash))
                    throw new InvalidDataException($"Client mod '{guid}' contained an invalid SHA-256 hash.");
                if (result.ContainsKey(guid))
                    throw new InvalidDataException($"Client mod list contained duplicate GUID '{guid}'.");

                result.Add(guid, new ModFingerprint(guid, version, hash));
            }
            return result;
        }

        internal static string ComputeSha256(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new FileNotFoundException("Mod DLL was not found.", path);

            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        private static bool IsSha256(string value)
        {
            if (value == null || value.Length != 64) return false;
            foreach (var c in value)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                    return false;
            return true;
        }
    }
}
