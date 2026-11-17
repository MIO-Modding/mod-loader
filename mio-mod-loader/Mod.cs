using JetBrains.Annotations;
using System.Reflection;

namespace MioModLoader;

[PublicAPI]
[UsedImplicitly(ImplicitUseTargetFlags.WithInheritors)]
public class Mod
{
    public Assembly Assembly { get; internal set; } = null!; // Set by ModLoader
    public string Name { get; internal set; } = null!; // Set by ModLoader
    public string Id { get; internal set; } = null!; // Set by ModLoader
    public string[] Dependencies { get; internal set; } = [];
    public ModType[] ModTypes { get; internal set; } = [];


    /// <summary>ANSI color for this mod's name in the log window (from mod.json "logColor", or picked from the id).</summary>
    internal string LogColor { get; set; } = "";

    public virtual void Initialize() { }

    public string GetModFolderPath()
    {
        return new FileInfo(Assembly.Location!).DirectoryName!;
    }

    public string GetModConfigPath()
    {
        return Path.Combine(ModLoader.ModsConfigPath, Id);
    }

    /// <summary>Logs a Debug line: hidden unless the game is started with --log-level debug.</summary>
    public void LogDebug(string message)
    {
        ModLoader.Log(this, message, LogLevel.Debug);
    }

    public void LogMessage(string message)
    {
        ModLoader.Log(this, message);
    }

    public void LogInfo(string message)
    {
        ModLoader.Log(this, message, LogLevel.Info);
    }

    public void LogWarning(string message)
    {
        ModLoader.Log(this, message, LogLevel.Warning);
    }

    public void LogError(string message)
    {
        ModLoader.Log(this, message, LogLevel.Error);
    }
}