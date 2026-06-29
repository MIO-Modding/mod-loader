using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace MioModLoader
{
    public abstract class Mod
    {
        public Assembly assembly;
        public string name;
        public string id;
        public string[] dependencies;
        public Mod(Assembly assembly, string name, string id, string[] dependencies)
        {
            this.assembly = assembly;
            this.name = name;
            this.id = id;
            this.dependencies = dependencies;
        }
        public abstract void Initialize();
        public string GetModFolderPath()
        {
            return Path.Combine(ModLoader.modsPath, id);
        }
        public string GetModConfigPath()
        {
            return Path.Combine(ModLoader.modsConfigPath, id);
        }
        public void LogMessage(string message)
        {
            ModLoader.LogMessage($"[{id}] {message}");
        }
    }
}
