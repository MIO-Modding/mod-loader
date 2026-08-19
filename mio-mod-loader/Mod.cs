using System.Reflection;

namespace MioModLoader;

public abstract class Mod
{
  public Assembly Assembly { get; internal set; }
  public string Name { get; internal set; }
  public string Id { get; internal set; }
  public string[] Dependencies { get; internal set; }
  public Mod(Assembly assembly, string name, string id, string[] dependencies)
  {
    Assembly = assembly;
    Name = name;
    Id = id;
    Dependencies = dependencies;
  }
  public abstract void Initialize();
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
