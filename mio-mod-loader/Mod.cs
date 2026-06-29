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
        public Mod(Assembly assembly)
        {
            this.assembly = assembly;
        }
        public abstract string GetId();
        public abstract string GetName();
        public abstract string[] GetDependencies();
        public abstract void Initialize();
        public string GetModFolderPath()
        {
            return Path.Combine(ModLoader.modsPath, GetId());
        }
        public string GetModConfigPath()
        {
            return Path.Combine(ModLoader.modsConfigPath, GetId());
        }
        public void LogMessage(string message)
        {
            ModLoader.LogMessage($"[{GetId()}] {message}");
        }
    }
}
