using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Common;

namespace MadFoundation;

// TODO: Auto refill option (e.g. for ores)
// TODO: Investigate tool switching issues

[AttributeUsage(AttributeTargets.Class)]
public class MadPatchAttribute(string configKey = "") : Attribute
{
    public string ConfigKey { get; } = configKey;
}

public class MadFoundationModSystem : ModSystem
{
    public  static ICoreAPI?            _api;
    private static readonly Harmony     _harmony = new ("org.gr1m.mods.vintagestory.MadFoundation.Harmony");
    private static bool                 _patched = false;
    private static bool                 _latePatched = false;

    // We need to be aggressive here because the mod More Piles runs a reverse patch in StartPre on a method we're transpiling, and we
    // want to transpile into it first.
    public override double ExecuteOrder() => 0;
    
    public override void StartPre(ICoreAPI api)
    {
        _api = api;

        if (_patched) return;
        CallPatchHandlers("RegisterPatches");
        _patched = true;
    }
    
    public override void AssetsFinalize(ICoreAPI api)
    {
        base.AssetsFinalize(api);
        
        if (_latePatched) return;
        CallPatchHandlers("RegisterLatePatches");
        _latePatched = true;
    }

    public override void Dispose()
    {
        if (!_patched && !_latePatched) return;
        _harmony.UnpatchAll();
        _patched = false;
    }

    private static void CallPatchHandlers(string handlerName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var patchTypes = assembly.GetTypes()
            .Select(t => (t, t.GetCustomAttribute<MadPatchAttribute>()))
            .OfType<(Type type, MadPatchAttribute attr)>()
            .ToArray();

        foreach (var patch in patchTypes)
        {
/*            if (!IsPatchEnabledInConfig(patch.attr.ConfigKey))
            {
                api.Logger.Notification($"[MadFoundation] Skipping disabled patch group: {patch.type.Name}");
                continue;
            }*/
            
            try
            {
                var method = patch.type.GetMethod(handlerName, BindingFlags.Public | BindingFlags.Static);
                method?.Invoke(null, [_api, _harmony]);
            }
            catch (TargetInvocationException ex)
            {
                _api?.Logger.Error($"[Mod] Runtime error applying patch {patch.type.Name}: {ex.InnerException?.Message}");
                _api?.Logger.Error(ex.InnerException?.StackTrace);
            }
            catch (Exception ex)
            {
                _api?.Logger.Error($"[Mod] Reflection error loading patch {patch.type.Name}: {ex.Message}");
            }
        }
    }
}