using BepInEx;
using Mono.Cecil;
using System;
using System.Collections.Generic;
using System.IO;

namespace FuriousAntiCheat
{
    internal static class ModManifest
    {
        private static readonly object BuildLock = new object();
        private static bool _isBuilt;

        public static string RootPath { get; private set; }
        public static string RequiredPath { get; private set; }
        public static string OptionalPath { get; private set; }

        public static Dictionary<string, ModFingerprint> Required { get; private set; } = Empty();
        public static Dictionary<string, ModFingerprint> Optional { get; private set; } = Empty();
        public static bool IsValid { get; private set; }

        public static void Initialize()
        {
            RootPath = Path.Combine(Paths.ConfigPath, Plugin.ModName);
            RequiredPath = Path.Combine(RootPath, "ModsObligatorios");
            OptionalPath = Path.Combine(RootPath, "ModsOpcionales");
            Directory.CreateDirectory(RequiredPath);
            Directory.CreateDirectory(OptionalPath);
        }

        public static void BuildOnce()
        {
            if (_isBuilt) return;
            lock (BuildLock)
            {
                if (_isBuilt) return;
                try
                {
                    BuildCore();
                }
                catch (Exception ex)
                {
                    Required = Empty();
                    Optional = Empty();
                    IsValid = false;
                    try { Plugin.Log.LogError($"Mod manifest build failed safely: {ex}"); }
                    catch { /* Never propagate a logging failure. */ }
                }
                finally
                {
                    _isBuilt = true;
                }
            }
        }

        private static void BuildCore()
        {
            var required = Empty();
            var optional = Empty();
            var fatalErrors = new List<string>();
            var warnings = new List<string>();

            if (ServerModSet.Mods.TryGetValue(Plugin.ModGuid, out var self))
                required.Add(self.Guid, self);
            else
                fatalErrors.Add("FuriousAntiCheat could not fingerprint its own DLL");

            AddFolder(RequiredPath, required, "required", true, fatalErrors, warnings);
            AddFolder(OptionalPath, optional, "optional", false, fatalErrors, warnings);

            foreach (var guid in required.Keys)
                if (optional.ContainsKey(guid))
                    fatalErrors.Add($"mod '{guid}' appears in both ModsObligatorios and ModsOpcionales");

            if (required.Count + optional.Count > ServerModSet.MaxMods)
                fatalErrors.Add($"manifest contains more than {ServerModSet.MaxMods} mods");

            Required = required;
            Optional = optional;
            IsValid = fatalErrors.Count == 0;

            foreach (var error in fatalErrors)
                Plugin.SafeLogError("Invalid mod manifest: " + error);
            foreach (var warning in warnings)
                Plugin.SafeLogWarning("Optional mod skipped: " + warning);
            try
            {
                Plugin.Log.LogInfo(
                    $"Mod manifest cached: required={required.Count}, optional={optional.Count}, " +
                    $"warnings={warnings.Count}, fatal-errors={fatalErrors.Count}, valid={IsValid}. " +
                    "Restart the server after changing catalog DLLs.");
            }
            catch { /* Logging is non-critical. */ }
        }

        private static void AddFolder(
            string folder,
            Dictionary<string, ModFingerprint> destination,
            string label,
            bool errorsAreFatal,
            List<string> fatalErrors,
            List<string> warnings)
        {
            string[] paths;
            try
            {
                paths = Directory.GetFiles(folder, "*.dll", SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex)
            {
                AddProblem(
                    $"could not enumerate {label} folder '{folder}': {ex.Message}",
                    errorsAreFatal,
                    fatalErrors,
                    warnings);
                return;
            }

            foreach (var path in paths)
            {
                try
                {
                    var hash = ServerModSet.ComputeSha256(path);
                    var mods = ReadPluginMetadata(path, hash);
                    if (mods.Count == 0)
                    {
                        AddProblem(
                            $"{label} DLL '{Path.GetFileName(path)}' does not contain a BepInEx plugin",
                            errorsAreFatal,
                            fatalErrors,
                            warnings);
                        continue;
                    }

                    foreach (var mod in mods)
                    {
                        if (destination.ContainsKey(mod.Guid))
                            AddProblem(
                                $"duplicate {label} mod GUID '{mod.Guid}' in '{Path.GetFileName(path)}'",
                                errorsAreFatal,
                                fatalErrors,
                                warnings);
                        else
                        {
                            destination.Add(mod.Guid, mod);
                            Plugin.SafeLogInfo(
                                $"Manifest {label}: file='{Path.GetFileName(path)}', " +
                                $"guid='{mod.Guid}', version='{mod.Version}'.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    AddProblem(
                        $"could not inspect {label} DLL '{Path.GetFileName(path)}': {ex.Message}",
                        errorsAreFatal,
                        fatalErrors,
                        warnings);
                }
            }
        }

        private static void AddProblem(
            string message,
            bool fatal,
            List<string> fatalErrors,
            List<string> warnings)
        {
            if (fatal) fatalErrors.Add(message);
            else warnings.Add(message);
        }

        private static List<ModFingerprint> ReadPluginMetadata(string path, string hash)
        {
            var result = new List<ModFingerprint>();
            using (var resolver = new DefaultAssemblyResolver())
            {
                AddSearchDirectory(resolver, Path.GetDirectoryName(path));
                AddSearchDirectory(resolver, Path.Combine(Paths.BepInExRootPath, "core"));
                AddSearchDirectory(resolver, Paths.ManagedPath);

                var parameters = new ReaderParameters
                {
                    AssemblyResolver = resolver,
                    InMemory = true,
                    ReadSymbols = false,
                    ReadingMode = ReadingMode.Immediate,
                };

                using (var assembly = AssemblyDefinition.ReadAssembly(path, parameters))
                {
                    foreach (var type in GetAllTypes(assembly.MainModule.Types))
                    {
                        foreach (var attribute in type.CustomAttributes)
                        {
                            if (attribute.AttributeType.FullName != "BepInEx.BepInPlugin") continue;
                            if (attribute.ConstructorArguments.Count < 3)
                                throw new InvalidDataException("BepInPlugin metadata is incomplete.");

                            var guid = attribute.ConstructorArguments[0].Value as string;
                            var version = attribute.ConstructorArguments[2].Value as string;
                            if (string.IsNullOrWhiteSpace(guid) || string.IsNullOrWhiteSpace(version))
                                throw new InvalidDataException("BepInPlugin GUID or version is empty.");

                            result.Add(new ModFingerprint(guid, version, hash));
                        }
                    }
                }
            }
            return result;
        }

        private static void AddSearchDirectory(DefaultAssemblyResolver resolver, string directory)
        {
            if (resolver == null || string.IsNullOrWhiteSpace(directory)) return;
            if (Directory.Exists(directory)) resolver.AddSearchDirectory(directory);
        }

        private static IEnumerable<TypeDefinition> GetAllTypes(IEnumerable<TypeDefinition> types)
        {
            foreach (var type in types)
            {
                yield return type;
                foreach (var nested in GetAllTypes(type.NestedTypes))
                {
                    yield return nested;
                }
            }
        }

        private static Dictionary<string, ModFingerprint> Empty() =>
            new Dictionary<string, ModFingerprint>(StringComparer.OrdinalIgnoreCase);
    }
}
