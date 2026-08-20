using JetBrains.Annotations;
using System.Reflection;

namespace MioModLoader;

[PublicAPI]
[UsedImplicitly(ImplicitUseTargetFlags.WithInheritors)]
public class Mod
{
    public Assembly Assembly { get; internal set; }
    public string Name { get; internal set; }
    public string Id { get; internal set; }
    public string[] Dependencies { get; internal set; } = [];
    public ModType[] ModTypes { get; internal set; } = [];

    public virtual void Initialize() { }

    public string GetModFolderPath()
    {
        return Path.Combine(ModLoader.ModsPath, Id);
    }

    public string GetModConfigPath()
    {
        return Path.Combine(ModLoader.ModsConfigPath, Id);
    }

    public void LogMessage(string message)
    {
        ModLoader.LogMessage($"[{Id}] {message}");
    }
}