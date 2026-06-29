using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

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
        [UnmanagedCallersOnly(EntryPoint = "LoadMods", CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
        public static void LoadModsPointers(IntPtr modsPathPtr, IntPtr modsConfigPathPtr, IntPtr logMessageMethod)
        {
            try
            {
                _cachedLogMessageMethod = Marshal.GetDelegateForFunctionPointer<LogMessageDelegate>(logMessageMethod);

                string path = Marshal.PtrToStringAnsi(modsPathPtr) ?? modsPath;
                string config = Marshal.PtrToStringAnsi(modsConfigPathPtr) ?? modsConfigPath;

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
        public static void LoadMods(string modsPath, string modsConfigPath)
        {
            ModLoader.modsPath = modsPath;
            ModLoader.modsConfigPath = modsConfigPath;

            LogLoaderMessage("Loading mods from " + modsPath);

            List<Mod> mods = new List<Mod>();
            List<Assembly> assemblies = new List<Assembly>();
            foreach (string i in Directory.GetDirectories(modsPath))
            {
                foreach (string j in Directory.GetFiles(i, "*.dll"))
                {
                    string dllPath = new FileInfo(j).FullName;
                    var context = new AssemblyLoadContext(name: Path.GetFileNameWithoutExtension(dllPath), isCollectible: true);
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
                        string expectedDependencyPath = Path.Combine(Path.GetDirectoryName(dllPath)!, $"{assemblyName.Name}.dll");
                        if (File.Exists(expectedDependencyPath))
                        {
                            return alc.LoadFromAssemblyPath(expectedDependencyPath);
                        }
                        return null;
                    };
                    assemblies.Add(context.LoadFromAssemblyPath(dllPath));
                }
            }
            foreach (var i in assemblies)
            {
                foreach (var type in i.DefinedTypes.Where(type => !type.IsAbstract && type.IsSubclassOf(typeof(Mod))).ToList())
                {
                    Mod? mod = Activator.CreateInstance(type, i) as Mod;
                    if (mod != null)
                    {
                        mods.Add(mod);
                    }
                }
            }
            List<Mod> loadedMods = new List<Mod>();
            List<Mod> rootMods = new List<Mod>();
            Dictionary<Mod, List<Mod>> dependantDict = new Dictionary<Mod, List<Mod>>();
            foreach (var i in mods)
            {
                List<Mod> dependants = mods.Where((j) => j.GetDependencies().Contains(i.GetId())).ToList();
                if (dependants.Count <= 0)
                {
                    rootMods.Add(i);
                } else
                {
                    dependantDict.Add(i, dependants);
                }
            }
            void LoadDependants(List<Mod> mods)
            {
                foreach (var i in mods)
                {
                    i.Initialize();
                    loadedMods.Add(i);
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
