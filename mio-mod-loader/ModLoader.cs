using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.Json.Nodes;

namespace MioModLoader
{
    public class ModLoader
    {
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate void LogMessageDelegate([MarshalAs(UnmanagedType.LPStr)] string message);
        private static LogMessageDelegate? _cachedLogMessageMethod;

        public static List<Assembly> loadedAssemblies = new List<Assembly>();
        public static List<Mod> loadedMods = new List<Mod>();
        public static string modsPath = "./modconfig";
        public static string modsConfigPath = "./mods";
        public static long mioMemoryAddress;
        [UnmanagedCallersOnly(EntryPoint = "LoadMods", CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
        public static void LoadModsPointers(IntPtr modsPathPtr, IntPtr modsConfigPathPtr, IntPtr logMessageMethod, IntPtr mioMemoryAddress)
        {
            try
            {
                ModLoader.mioMemoryAddress = mioMemoryAddress;
                _cachedLogMessageMethod = Marshal.GetDelegateForFunctionPointer<LogMessageDelegate>(logMessageMethod);

                string path = Marshal.PtrToStringAnsi(modsPathPtr) ?? modsPath;
                string config = Marshal.PtrToStringAnsi(modsConfigPathPtr) ?? modsConfigPath;

                LoadLibraries();
                LoadMods(path, config);
            }
            catch (Exception ex)
            {
                LogLoaderMessage($"[FATAL CRASH] Mod execution handler failed: {ex.Message}\n{ex.StackTrace}");
            }
        }
        public static void LogMessage(string message)
        {
            if (_cachedLogMessageMethod != null)
            {
                _cachedLogMessageMethod(message);
            }
        }
        public static void LogLoaderMessage(string message)
        {
            LogMessage($"[LOADER] {message}");
        }
        private static void LoadLibraries()
        {
            string modFolder = new FileInfo(Assembly.GetExecutingAssembly().Location!).DirectoryName!;
            string nativeFolder = Path.Combine(modFolder, "runtimes", "win-x64", "native");

            string[] natives = ["asmjit", "asmtk", "Zydis", "PolyHook_2"];
            foreach (var i in natives)
            {
                NativeLibrary.TryLoad(Path.Combine(nativeFolder, i + ".dll"), out _);
            }
        }
        private static Dictionary<string, Assembly> allAssemblies = new Dictionary<string, Assembly>();
        public static void LoadMods(string modsPath, string modsConfigPath)
        {
            ModLoader.modsPath = modsPath;
            ModLoader.modsConfigPath = modsConfigPath;

            LogLoaderMessage("Loading mods from " + modsPath);

            Dictionary<string, (string assemblyPath, string modId, string modName, string[] dependencies)> modLoadInfo = new Dictionary<string, (string assemblyPath, string modId, string modName, string[] dependencies)>();

            List<Assembly> assemblies = new List<Assembly>();
            foreach (string i in Directory.GetDirectories(modsPath))
            {
                string modInfo = Path.Combine(i, "mod.json");
                if (File.Exists(modInfo))
                {
                    var json = JsonObject.Parse(File.ReadAllText(modInfo));
                    if (json is JsonObject obj)
                    {
                        string id = obj["id"].GetValue<string>();
                        string name = obj["name"].GetValue<string>();
                        string main = new FileInfo(Path.Combine(i, obj["main"].GetValue<string>())).FullName;
                        List<string> dependencies = new List<string>();
                        foreach (var j in obj["dependencies"].AsArray())
                        {
                            dependencies.Add(j.GetValue<string>());
                        }
                        modLoadInfo.Add(id, (main, id, name, dependencies.ToArray()));
                    }
                }
            }
            Dictionary<string, string> modsMissingDependencies = new Dictionary<string, string>();
            foreach (var i in modLoadInfo)
            {
                List<string> missing = new List<string>();
                foreach (var j in i.Value.dependencies)
                {
                    if (!modLoadInfo.ContainsKey(j))
                    {
                        missing.Add(j);
                    }
                }
                if (missing.Count > 0)
                {
                    modsMissingDependencies.Add(i.Key, string.Join(", ", missing));
                }
            }
            if (modsMissingDependencies.Count > 0)
            {
                string exceptionStr = "Mods are missing dependencies:";
                foreach (var i in modsMissingDependencies)
                {
                    exceptionStr += $"\n{i.Key} - {i.Value}";
                }
                throw new Exception(exceptionStr);
            }
            List<Mod> loadedMods = new List<Mod>();
            List<string> rootMods = new List<string>();
            Dictionary<string, List<string>> dependantDict = new Dictionary<string, List<string>>();
            foreach (var i in modLoadInfo)
            {
                List<string> dependants = modLoadInfo.Where((j) => j.Value.dependencies.Contains(i.Key)).Select((j) => j.Key).ToList();
                if (i.Value.dependencies.Length <= 0)
                {
                    rootMods.Add(i.Key);
                }
                dependantDict.Add(i.Key, dependants);
            }
            void LoadDependants(List<string> mods)
            {
                foreach (var i in mods)
                {
                    LogLoaderMessage($"Loading Mod {i}");
                    string dllPath = modLoadInfo[i].assemblyPath;
                    var context = new AssemblyLoadContext(name: Path.GetFileNameWithoutExtension(dllPath));
                    context.Resolving += (alc, assemblyName) =>
                    {
                        if (assemblyName.Name == "MioModLoader")
                        {
                            return Assembly.GetExecutingAssembly();
                        }
                        var assembly = assemblies.FirstOrDefault((i) => i.GetName().Name == assemblyName.Name);
                        if (assembly != null)
                        {
                            return assembly;
                        }
                        if (allAssemblies.TryGetValue(assemblyName.Name!, out Assembly? value))
                        {
                            return value;
                        }
                        string expectedDependencyPath = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!, $"{assemblyName.Name}.dll");
                        if (File.Exists(expectedDependencyPath))
                        {
                            var result = alc.LoadFromAssemblyPath(expectedDependencyPath);
                            allAssemblies.Add(assemblyName.Name!, result);
                            return result;
                        }
                        expectedDependencyPath = Path.Combine(Path.GetDirectoryName(dllPath)!, $"{assemblyName.Name}.dll");
                        if (File.Exists(expectedDependencyPath))
                        {
                            var result = alc.LoadFromAssemblyPath(expectedDependencyPath);
                            allAssemblies.Add(assemblyName.Name!, result);
                            return result;
                        }
                        return null;
                    };
                    context.ResolvingUnmanagedDll += (assembly, libraryName) =>
                    {
                        string modFolder = new FileInfo(assembly.Location!).DirectoryName!;
                        string nativeFolder = Path.Combine(modFolder, "win-x64", "native");
                        string expectedDllPath = Path.Combine(nativeFolder, $"{libraryName}.dll");

                        nint handle = nint.Zero;
                        if (File.Exists(expectedDllPath))
                        {
                            NativeLibrary.TryLoad(expectedDllPath, out handle);
                        }

                        modFolder = new FileInfo(Assembly.GetExecutingAssembly().Location!).DirectoryName!;
                        nativeFolder = Path.Combine(modFolder, "win-x64", "native");
                        expectedDllPath = Path.Combine(nativeFolder, $"{libraryName}.dll");
                        if (File.Exists(expectedDllPath))
                        {
                            NativeLibrary.TryLoad(expectedDllPath, out handle);
                        }
                        return handle;
                    };
                    Assembly assembly = context.LoadFromAssemblyPath(dllPath);
                    assemblies.Add(assembly);
                    foreach (var type in assembly.DefinedTypes.Where(type => !type.IsAbstract && type.IsSubclassOf(typeof(Mod))).ToList())
                    {
                        Mod? mod = Activator.CreateInstance(type, assembly, modLoadInfo[i].modName, modLoadInfo[i].modId, modLoadInfo[i].dependencies) as Mod;
                        if (mod != null)
                        {
                            loadedMods.Add(mod);
                            mod.Initialize();
                        }
                    }
                    if (dependantDict.ContainsKey(i))
                    {
                        LoadDependants(dependantDict[i]);
                    }
                }
            }
            LoadDependants(rootMods);
            LogLoaderMessage($"Finished loading {loadedMods.Count} mods from " + modsPath);
            ModLoader.loadedMods = loadedMods;
            ModLoader.loadedAssemblies = assemblies;
        }
    }
}
