using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System;

namespace FuriousAntiCheat
{
    [BepInPlugin(ModGuid, ModName, ModVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string ModGuid    = "com.furiousvalheim.anticheat";
        public const string ModName    = "FuriousAntiCheat";
        public const string ModVersion = "2.3.2";

        internal static ManualLogSource Log;
        internal static bool IsOperational { get; private set; }
        internal static bool ShouldRun => IsOperational && Enabled != null && Enabled.Value;

        internal static void SafeLogError(string message)
        {
            try { Log?.LogError(message); }
            catch { /* Logging must never affect the host process. */ }
        }

        internal static void SafeLogWarning(string message)
        {
            try { Log?.LogWarning(message); }
            catch { /* Logging must never affect the host process. */ }
        }

        internal static void SafeLogInfo(string message)
        {
            try { Log?.LogInfo(message); }
            catch { /* Logging must never affect the host process. */ }
        }

        internal static ConfigEntry<bool>   Enabled;
        internal static ConfigEntry<string> EnforcementMode;
        internal static ConfigEntry<float>  KickTimeoutSeconds;
        internal static ConfigEntry<int>    HandshakeRetries;
        internal static ConfigEntry<int>    MaxDisplayedProblems;

        internal static ConfigEntry<bool>   LogAcceptedConnections;

        public void Awake()
        {
            Log = Logger;
            IsOperational = false;
            try
            {
                Enabled = Config.Bind("1 - General", "Enabled", true,
                "If false, no checks run and no peers are kicked.");
            EnforcementMode = Config.Bind("1 - General", "EnforcementMode", "Enforce",
                new ConfigDescription(
                    "Enforce kicks mismatched clients. AuditOnly logs mismatches but allows connections.",
                    new AcceptableValueList<string>("Enforce", "AuditOnly")));
            KickTimeoutSeconds = Config.Bind("1 - General", "KickTimeoutSeconds", 15f,
                "Seconds to wait for a connecting client's mod-list reply before kicking. " +
                "Vanilla / non-FuriousAntiCheat clients will hit this and be kicked.");
            HandshakeRetries = Config.Bind("1 - General", "HandshakeRetries", 1,
                "Number of additional mod-list requests after a timeout. Default 1 gives slow clients one retry.");

            MaxDisplayedProblems = Config.Bind("2 - Strictness", "MaxDisplayedProblems", 6,
                "Maximum mismatch reasons included in logs and player messages.");

            LogAcceptedConnections = Config.Bind("3 - Feedback", "LogAcceptedConnections", false,
                "If true, log every client that passes mod validation.");

                NormalizeConfig();

                ModManifest.Initialize();

                new Harmony(ModGuid).PatchAll();
                IsOperational = true;
                Log.LogInfo($"{ModName} v{ModVersion} loaded. Enabled={Enabled.Value}; mode={EnforcementMode.Value}; " +
                        $"timeout={KickTimeoutSeconds.Value:0.#}s; retries={HandshakeRetries.Value}; " +
                        $"mod hashes will be cached during startup; max-problems={MaxDisplayedProblems.Value}; " +
                        $"accepted-logging={LogAcceptedConnections.Value}; detailed-rejections=true.");
            }
            catch (Exception ex)
            {
                IsOperational = false;
                try { Logger.LogError($"{ModName} initialization failed; anti-cheat disabled safely: {ex}"); }
                catch { /* Logging must never rethrow into Unity. */ }
            }
        }

        public void Start()
        {
            if (!IsOperational) return;
            try
            {
                ServerModSet.BuildOnce();
                ModManifest.BuildOnce();
                Log.LogInfo(
                    $"Startup hash cache ready: loaded={ServerModSet.Count}; " +
                    $"required={ModManifest.Required.Count}; optional={ModManifest.Optional.Count}; " +
                    $"manifest-valid={ModManifest.IsValid}.");
            }
            catch (Exception ex)
            {
                SafeLogError($"Failed to initialize the startup hash cache: {ex}");
            }
        }

        private static void NormalizeConfig()
        {
            if (float.IsNaN(KickTimeoutSeconds.Value) ||
                float.IsInfinity(KickTimeoutSeconds.Value) ||
                KickTimeoutSeconds.Value < 1f)
            {
                Log.LogWarning("KickTimeoutSeconds must be a finite value of at least 1 second; using 15.");
                KickTimeoutSeconds.Value = 15f;
            }

            if (HandshakeRetries.Value < 0 || HandshakeRetries.Value > 5)
            {
                Log.LogWarning("HandshakeRetries must be between 0 and 5; using 1.");
                HandshakeRetries.Value = 1;
            }

            if (MaxDisplayedProblems.Value < 1 || MaxDisplayedProblems.Value > 50)
            {
                Log.LogWarning("MaxDisplayedProblems must be between 1 and 50; using 6.");
                MaxDisplayedProblems.Value = 6;
            }

            if (!string.Equals(EnforcementMode.Value, "Enforce", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(EnforcementMode.Value, "AuditOnly", StringComparison.OrdinalIgnoreCase))
            {
                Log.LogWarning($"Unknown EnforcementMode '{EnforcementMode.Value}'. Using Enforce.");
                EnforcementMode.Value = "Enforce";
            }

        }
    }
}
