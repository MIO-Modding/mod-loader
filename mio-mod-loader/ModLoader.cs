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
    private delegate void LogMessageDelegate([MarshalAs(UnmanagedType.LPUTF8Str)] string message);

    private static LogMessageDelegate? _cachedLogMessageMethod;


    /// <summary>
    /// Old way to send log lines to the game's log file. Still honored, with plain lines, when
    /// <see cref="FileLogSink"/> isn't set; replaced by <see cref="FileLogSink"/>.
    /// </summary>
    [Obsolete("Set FileLogSink instead. Log lines are shown in the log window by the loader itself.")]
    public static Action<string>? logOverride;

    private static Func<string, bool>? _fileLogSink;

    /// <summary>
    /// Writes one plain log line (no trailing newline) to the game's log file, returning false if it
    /// can't yet (e.g. the game hasn't opened the file). Lines are kept until it can, so nothing logged
    /// early is lost. Set by the modding api.
    /// </summary>
    public static Func<string, bool>? FileLogSink
    {
        get => _fileLogSink;
        set
        {
            _fileLogSink = value;
            Logging.Flush();
        }
    }

    /// <summary>Writes log lines kept while <see cref="FileLogSink"/> couldn't take them.</summary>
    public static void FlushFileLog() => Logging.Flush();
    public static List<Assembly> LoadedAssemblies { get; private set; } = [];
    public static List<Mod> LoadedMods { get; private set; } = [];
    public static string ModsPath { get; private set; } = "./mods";
    public static string ModsConfigPath { get; private set; } = "./modconfig";
    public static long MioMemoryAddress { get; private set; }


    /// <summary>Flags from the native loader; keep in step with LogFlags in mod_loader.cpp.</summary>
    [Flags]
    private enum LogFlags
    {
        Rich = 1 << 0,
        GameLogs = 1 << 1,
        LevelShift = 2,       // bits 2-3: minimum LogLevel
        LevelMask = 3 << 2,
    }

    /// <summary>
    /// The least important level that's logged; lower lines are dropped from the window and the file.
    /// Set at launch with --log-level debug|info|warning|error (default info), and changeable at runtime.
    /// </summary>
    public static LogLevel MinimumLogLevel
    {
        get => Logging.MinimumLevel;
        set => Logging.MinimumLevel = value;
    }

    /// <summary>
    /// True if lines at <paramref name="level"/> are logged. Use it to skip building an expensive
    /// message, e.g. <c>if (ModLoader.IsLogLevelEnabled(LogLevel.Debug)) LogDebug(Dump());</c>.
    /// </summary>
    public static bool IsLogLevelEnabled(LogLevel level) => level >= Logging.MinimumLevel;

    [UnmanagedCallersOnly(EntryPoint = "LoadMods", CallConvs = [typeof(CallConvCdecl)])]
    public static void LoadModsPointers(IntPtr modsPathPtr, IntPtr modsConfigPathPtr, IntPtr logMessageMethod,
        IntPtr mioMemoryAddress, int logFlags)
    {
        try
        {
            MioMemoryAddress = mioMemoryAddress;
            _cachedLogMessageMethod = Marshal.GetDelegateForFunctionPointer<LogMessageDelegate>(logMessageMethod);
            Logging.Rich = ((LogFlags)logFlags).HasFlag(LogFlags.Rich);
            Logging.MinimumLevel = (LogLevel)((logFlags & (int)LogFlags.LevelMask) >> (int)LogFlags.LevelShift);
            Logging.ConsoleSink = line => _cachedLogMessageMethod(line);

            // Standard output carries the game's output now; route Console.WriteLine to the log instead.
            Console.SetOut(new ConsoleLogWriter());

            string path = Marshal.PtrToStringAnsi(modsPathPtr) ?? ModsPath;
            string config = Marshal.PtrToStringAnsi(modsConfigPathPtr) ?? ModsConfigPath;

            LoadLibraries();
            LoadMods(path, config);
        }
        catch (Exception ex)
        {
            LogLoaderError($"[FATAL CRASH] Mod execution handler failed: {ex.Message}\n{ex.StackTrace}");

            if (ex is MemberAccessException or TypeLoadException)
            {
                LogLoaderWarning("This is likely due to a mod or the Modding API being out of date. ");
                LogLoaderWarning("Please ensure you are using the latest version of all mods and the Modding API.");
            }
        }
    }

    /// <summary>Logs a line with no source tag.</summary>
    public static void LogMessage(string message) => Logging.Write(null, null, LogLevel.Info, message);

    /// <summary>Logs a line tagged with <paramref name="source"/>, e.g. a library's own name.</summary>
    public static void Log(string source, string message, LogLevel level = LogLevel.Info) =>
        Logging.Write(source, Logging.ColorFor(source, null), level, message);

    /// <summary>Logs a line for a mod, tagged with its name and colored with its log color.</summary>
    public static void Log(Mod mod, string message, LogLevel level = LogLevel.Info) =>
        Logging.Write(mod.Name, mod.LogColor, level, message);

    public static void LogLoaderDebug(string message) =>
        Logging.Write("LOADER", Logging.LoaderSourceColor, LogLevel.Debug, message);

    public static void LogLoaderMessage(string message) =>
        Logging.Write("LOADER", Logging.LoaderSourceColor, LogLevel.Info, message);

    public static void LogLoaderWarning(string message) =>
        Logging.Write("LOADER", Logging.LoaderSourceColor, LogLevel.Warning, message);

    public static void LogLoaderError(string message) =>
        Logging.Write("LOADER", Logging.LoaderSourceColor, LogLevel.Error, message);

    private static void LoadLibraries()
    {
        string modFolder = new FileInfo(Assembly.GetExecutingAssembly().Location).DirectoryName!;
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

        ModLoadContext ctx = new();

        foreach (string i in Directory.GetDirectories(modsPath))
        {
            string modInfo = Path.Combine(i, "mod.json");
            if (!File.Exists(modInfo))
            {
                continue;
            }

            if (JsonNode.Parse(File.ReadAllText(modInfo)) is not JsonObject obj)
            {
                LogLoaderWarning($"Skipping mod in {i}, mod.json is not a valid JSON object.");
                continue;
            }

            if (obj["name"]?.GetValue<string>() is not { } name)
            {
                LogLoaderWarning($"Skipping mod in {i}, mod.json is missing field \"name\".");
                continue;
            }

            if (obj["id"]?.GetValue<string>() is not { } id)
            {
                LogLoaderWarning($"Skipping mod {name}: mod.json is missing field \"id\".");
                continue;
            }

            if (obj["main"]?.GetValue<string>() is not { } mainFile)
            {
                LogLoaderWarning($"Skipping mod {name}: mod.json is missing field \"main\".");
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
                    if (jsonDependencies[index]?.GetValue<string>() is not { } dependency)
                    {
                        LogLoaderWarning(
                            $"Skipping mod {name}: mod.json dependencies has an invalid dependency at index {index}");
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

            string? logColor = obj["logColor"] is JsonValue colorValue && colorValue.TryGetValue(out string? c) ? c : null;

            ctx.ModInfo.Add(id, new ModInfo(main, id, name, dependencies, logColor));
        }

        Dictionary<string, string> modsMissingDependencies = [];
        foreach ((string key, ModInfo modInfo) in ctx.ModInfo)
        {
            List<string> missing = [];
            foreach (string j in modInfo.Dependencies)
            {
                if (!ctx.ModInfo.ContainsKey(j))
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

        List<string> rootMods = [];
        foreach ((string key, ModInfo modInfo) in ctx.ModInfo)
        {
            string[] dependents = ctx.ModInfo
                .Where(j => j.Value.Dependencies.Contains(key))
                .Select(j => j.Key).ToArray();
            if (modInfo.Dependencies.Length <= 0)
            {
                rootMods.Add(key);
            }

            ctx.Dependents.Add(key, dependents);
        }

        LoadDependents(rootMods.ToArray(), ctx);
        LogLoaderMessage($"Finished loading {ctx.LoadedMods.Count} mods from " + modsPath);
        LoadedMods = ctx.LoadedMods;
        LoadedAssemblies = ctx.Assemblies;
    }

    private static void LoadDependents(string[] mods, ModLoadContext ctx)
    {
        foreach (string i in mods)
        {
            LogLoaderMessage($"Loading Mod {i}");
            string dllPath = ctx.ModInfo[i].AssemblyPath;
            AssemblyLoadContext context = new(name: Path.GetFileNameWithoutExtension(dllPath));
            context.Resolving += (alc, assemblyName) =>
            {
                string name = assemblyName.Name!;
                if (name == "MioModLoader")
                {
                    return Assembly.GetExecutingAssembly();
                }

                Assembly? assembly = ctx.Assemblies.FirstOrDefault(a => a.GetName().Name == name);
                if (assembly != null)
                {
                    return assembly;
                }

                if (AllAssemblies.TryGetValue(name, out Assembly? value))
                {
                    return value;
                }

                string expectedDependencyPath =
                    Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!, $"{name}.dll");
                if (File.Exists(expectedDependencyPath))
                {
                    Assembly result = alc.LoadFromAssemblyPath(expectedDependencyPath);
                    AllAssemblies.Add(name, result);
                    return result;
                }

                expectedDependencyPath = Path.Combine(Path.GetDirectoryName(dllPath)!, $"{name}.dll");
                if (File.Exists(expectedDependencyPath))
                {
                    Assembly result = alc.LoadFromAssemblyPath(expectedDependencyPath);
                    AllAssemblies.Add(name, result);
                    return result;
                }

                return null;
            };
            context.ResolvingUnmanagedDll += static (assembly, libraryName) =>
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
            ctx.Assemblies.Add(assembly);

            if (InstantiateMod(assembly, ctx.ModInfo[i]) is not { } mod)
            {
                continue;
            }

            ctx.LoadedMods.Add(mod);

            mod.ModTypes = InstantiateModTypes(mod);
            foreach (ModType modType in mod.ModTypes)
            {
                modType.Initialize();
            }

            mod.Initialize();
            if (ctx.Dependents.TryGetValue(i, out string[]? dependents))
            {
                LoadDependents(dependents, ctx);
            }
        }
    }

    private static Mod? InstantiateMod(Assembly assembly, ModInfo modInfo)
    {
        const BindingFlags bindingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        TypeInfo? modType = assembly.DefinedTypes.SingleOrDefault(t => !t.IsAbstract && t.IsSubclassOf(typeof(Mod)));
        Mod mod;
        if (modType is null)
        {
            mod = new Mod();
        }
        else if (modType.GetConstructor(bindingFlags, Type.EmptyTypes) is { } ctor)
        {
            mod = (Mod)ctor.Invoke(null);
        }
        else
        {
            LogLoaderWarning(
                $"Skipping mod {modInfo.Name}: Mod type {modType.Name} does not have a parameterless constructor.");
            return null;
        }

        mod.Assembly = assembly;
        mod.Name = modInfo.Name;
        mod.Id = modInfo.Id;
        mod.Dependencies = modInfo.Dependencies;
        mod.LogColor = Logging.ColorFor(modInfo.Id, modInfo.LogColor);
        return mod;
    }

    private static ModType[] InstantiateModTypes(Mod mod)
    {
        const BindingFlags bindingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        List<ModType> modTypes = [];
        foreach (TypeInfo type in
                 mod.Assembly.DefinedTypes.Where(t => !t.IsAbstract && t.IsSubclassOf(typeof(ModType))))
        {
            if (type.GetConstructor(bindingFlags, Type.EmptyTypes) is { } ctor)
            {
                ModType instance = (ModType)ctor.Invoke(null);
                instance.Mod = mod;
                if (instance.IsLoadingEnabled)
                {
                    modTypes.Add(instance);
                }
            }
        }

        return modTypes.ToArray();
    }

}
internal class ModLoadContext
{
    public readonly Dictionary<string, ModInfo> ModInfo = [];
    public readonly List<Assembly> Assemblies = [];
    public readonly List<Mod> LoadedMods = [];
    public readonly Dictionary<string, string[]> Dependents = [];
}

internal record ModInfo(string AssemblyPath, string Id, string Name, string[] Dependencies, string? LogColor);
