namespace MioModLoader;

public abstract class ModType
{
    public Mod Mod { get; internal set; }

    public virtual bool IsLoadingEnabled => true;

    public virtual void Initialize() { }
}