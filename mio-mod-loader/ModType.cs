using JetBrains.Annotations;

namespace MioModLoader;

[PublicAPI]
[UsedImplicitly(ImplicitUseTargetFlags.WithInheritors)]
public abstract class ModType
{
    public Mod Mod { get; internal set; } = null!; // Set by ModLoader

    public virtual bool IsLoadingEnabled => true;

    public virtual void Initialize() { }
}