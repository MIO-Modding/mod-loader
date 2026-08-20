using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json.Nodes;

namespace MioModLoader;

public static class ModLoader
{
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void LogMessageDelegate([MarshalAs(UnmanagedType.LPStr)] string message);
    private static LogMessageDelegate? _cachedLogMessageMethod;

    public static List<Assembly> LoadedAssemblies { get; private set; } = [];
    public static List<Mod> LoadedMods { get; private set; } = [];
    public static string ModsPath { get; private set; } = "./mods";
    public static string ModsConfigPath { get; private set; } = "./modconfig";
    public static long MioMemoryAddress { get; private set; }
    [UnmanagedCallersOnly(EntryPoint = "LoadMods", CallConvs = [typeof(CallConvCdecl)])]
    public static void LoadModsPointers(IntPtr modsPathPtr, IntPtr modsConfigPathPtr, IntPtr logMessageMethod, IntPtr mioMemoryAddress)
    {
        try
        {
            MioMemoryAddress = mioMemoryAddress;
            _cachedLogMessageMethod = Marshal.GetDelegateForFunctionPointer<LogMessageDelegate>(logMessageMethod);

            string path = Marshal.PtrToStringAnsi(modsPathPtr) ?? ModsPath;
            string config = Marshal.PtrToStringAnsi(modsConfigPathPtr) ?? ModsConfigPath;

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
        _cachedLogMessageMethod?.Invoke(message);
    }
    public static void LogLoaderMessage(string message)
    {
        LogMessage($"[LOADER] {message}");
    }
    private static void LoadLibraries()
    {
        string modFolder = new FileInfo(Assembly.GetExecutingAssembly().Location!).DirectoryName!;
        string nativeFolder = Path.Combine(modFolder, "runtimes", "win-x64", "native");

        ReadOnlySpan<string> natives = ["asmjit", "asmtk", "Zydis", "PolyHook_2"];
        foreach (string i in natives)
        {
            NativeLibrary.TryLoad(Path.Combine(nativeFolder, i + ".dll"), out _);
        }
    }
    private static readonly Dictionary<string, Assembly> AllAssemblies = [];
    public static void LoadMods(string modsPath, string modsConfigPath)
    {
        ModsPath = modsPath;
        ModsConfigPath = modsConfigPath;

        LogLoaderMessage("Loading mods from " + modsPath);

        Dictionary<string, (string assemblyPath, string modId, string modName, string[] dependencies)> modLoadInfo = new();

        List<Assembly> assemblies = [];
        foreach (string i in Directory.GetDirectories(modsPath))
        {
            string modInfo = Path.Combine(i, "mod.json");
            if (!File.Exists(modInfo))
            {
                continue;
            }

            if (JsonNode.Parse(File.ReadAllText(modInfo)) is not JsonObject obj)
            {
                continue;
            }

            if (obj["name"]?.GetValue<string>() is not { } name)
            {
                LogLoaderMessage($"Mod in {i} is missing a name, skipping");
                continue;
            }

            if (obj["id"]?.GetValue<string>() is not { } id)
            {
                LogLoaderMessage($"Mod {name} is missing an id, skipping");
                continue;
            }

            if (obj["main"]?.GetValue<string>() is not { } mainFile)
            {
                LogLoaderMessage($"Mod {name} is missing a main file, skipping");
                continue;
            }

            string main = new FileInfo(Path.Combine(i, mainFile)).FullName;

            string[] dependencies = [];
            bool validDependencies = true;
            if (obj["dependencies"]?.AsArray() is { Count: > 0 } jsonDependencies)
            {
                dependencies = new string[jsonDependencies.Count];
                for (int index = 0; index < jsonDependencies.Count; index++)
                {
                    if (jsonDependencies[index]?.GetValue<string>() is not {} dependency)
                    {
                        LogLoaderMessage($"Mod {name} has an invalid dependency at index {index}");
                        validDependencies = false;
                        break;
                    }

                    dependencies[index] = dependency;
                }
            }

            if (!validDependencies)
            {
                continue;
            }

            modLoadInfo.Add(id, (main, id, name, dependencies));
        }
        Dictionary<string, string> modsMissingDependencies = [];
        foreach ((string key, (_, _, _, string[] dependencies)) in modLoadInfo)
        {
            List<string> missing = [];
            foreach (string j in dependencies)
            {
                if (!modLoadInfo.ContainsKey(j))
                {
                    missing.Add(j);
                }
            }
            if (missing.Count > 0)
            {
                modsMissingDependencies.Add(key, string.Join(", ", missing));
            }
        }
        if (modsMissingDependencies.Count > 0)
        {
            StringBuilder sb = new("Mods are missing dependencies:");
            foreach ((string key, string value) in modsMissingDependencies)
            {
                sb.Append($"\n{key} - {value}");
            }
            throw new Exception(sb.ToString());
        }
        List<Mod> loadedMods = [];
        List<string> rootMods = [];
        Dictionary<string, string[]> dependantDict = [];
        foreach ((string key, (_, _, _, string[] dependencies)) in modLoadInfo)
        {
            string[] dependants = modLoadInfo
                .Where(j => j.Value.dependencies.Contains(key))
                .Select(j => j.Key).ToArray();
            if (dependencies.Length <= 0)
            {
                rootMods.Add(key);
            }
            dependantDict.Add(key, dependants);
        }
        LoadDependants(rootMods.ToArray());
        LogLoaderMessage($"Finished loading {loadedMods.Count} mods from " + modsPath);
        LoadedMods = loadedMods;
        LoadedAssemblies = assemblies;
        return;

        void LoadDependants(string[] mods)
        {
            foreach (string i in mods)
            {
                LogLoaderMessage($"Loading Mod {i}");
                string dllPath = modLoadInfo[i].assemblyPath;
                AssemblyLoadContext context = new(name: Path.GetFileNameWithoutExtension(dllPath));
                context.Resolving += (alc, assemblyName) =>
                {
                    if (assemblyName.Name == "MioModLoader")
                    {
                        return Assembly.GetExecutingAssembly();
                    }
                    Assembly? assembly = assemblies.FirstOrDefault(a => a.GetName().Name == assemblyName.Name);
                    if (assembly != null)
                    {
                        return assembly;
                    }
                    if (AllAssemblies.TryGetValue(assemblyName.Name!, out Assembly? value))
                    {
                        return value;
                    }
                    string expectedDependencyPath = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!, $"{assemblyName.Name}.dll");
                    if (File.Exists(expectedDependencyPath))
                    {
                        Assembly result = alc.LoadFromAssemblyPath(expectedDependencyPath);
                        AllAssemblies.Add(assemblyName.Name!, result);
                        return result;
                    }
                    expectedDependencyPath = Path.Combine(Path.GetDirectoryName(dllPath)!, $"{assemblyName.Name}.dll");
                    if (File.Exists(expectedDependencyPath))
                    {
                        Assembly result = alc.LoadFromAssemblyPath(expectedDependencyPath);
                        AllAssemblies.Add(assemblyName.Name!, result);
                        return result;
                    }
                    return null;
                };
                context.ResolvingUnmanagedDll += (assembly, libraryName) =>
                {
                    string modFolder = new FileInfo(assembly.Location).DirectoryName!;
                    string nativeFolder = Path.Combine(modFolder, "win-x64", "native");
                    string expectedDllPath = Path.Combine(nativeFolder, $"{libraryName}.dll");

                    nint handle = nint.Zero;
                    if (File.Exists(expectedDllPath))
                    {
                        NativeLibrary.TryLoad(expectedDllPath, out handle);
                    }

                    modFolder = new FileInfo(Assembly.GetExecutingAssembly().Location).DirectoryName!;
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
                foreach (TypeInfo type in assembly.DefinedTypes.Where(type => !type.IsAbstract && type.IsSubclassOf(typeof(Mod))).ToList())
                {
                    if (Activator.CreateInstance(type, assembly, modLoadInfo[i].modName, modLoadInfo[i].modId, modLoadInfo[i].dependencies) is Mod mod)
                    {
                        loadedMods.Add(mod);
                        mod.Initialize();
                    }
                }
                if (dependantDict.TryGetValue(i, out string[]? dependent))
                {
                    LoadDependants(dependent);
                }
            }
        }
    }
}
