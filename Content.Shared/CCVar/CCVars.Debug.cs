using Robust.Shared.Configuration;

namespace Content.Shared.CCVar;

public sealed partial class CCVars
{
    /// <summary>
    /// Component to be inspected using the "Quick Inspect Component" keybind.
    /// Set by the "quickinspect" command.
    /// </summary>
    public static readonly CVarDef<string> DebugQuickInspect =
        CVarDef.Create("debug.quick_inspect", "", CVar.CLIENTONLY | CVar.ARCHIVE);

    /// <summary>
    /// When enabled, every mob condition check re-polls the producers and logs an error if the cached conditions
    /// are stale. Expensive; meant for integration tests.
    /// </summary>
    public static readonly CVarDef<bool> MobConditionsVerify =
        CVarDef.Create("debug.mob_conditions_verify", false, CVar.SERVER);
}
