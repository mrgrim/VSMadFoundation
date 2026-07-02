using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace MadFoundation;

[MadPatch]
public class InWorldContainerTransitionFix
{
    public static void RegisterPatches(ICoreAPI api, Harmony harmony)
    {
        harmony.Patch(
            AccessTools.Method(typeof(InWorldContainer), "OnTick"),
            transpiler: new HarmonyMethod(AccessTools.Method(typeof(InWorldContainerTransitionFix),
                nameof(OnTickTranspiler))));
    }
    
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "onRequireSyncToClient")]
    static extern ref Action GetOnRequireSyncToClient(InWorldContainer _);
    
    public static void SyncToClientIfDirty(InWorldContainer __instance)
    {
        if (__instance.Inventory.IsDirty) GetOnRequireSyncToClient(__instance)();
    }
    
    private static IEnumerable<CodeInstruction> OnTickTranspiler(
        IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        var matcher = new CodeMatcher(instructions);
        
        matcher.MatchStartForward(
                new CodeMatch(OpCodes.Ldarg_0),
                new CodeMatch(ci =>
                    ci.LoadsField(AccessTools.Field(typeof(InWorldContainer), "onRequireSyncToClient"))),
                new CodeMatch(OpCodes.Callvirt))
            .ThrowIfInvalid($"[Mad Foundation] Could not locate starting signature in {original.DeclaringType}.{original.Name}");

        // Delete the call to onRequireSyncToClient in the transition processing loop.
        matcher.RemoveInstructions(3);
        
        matcher.MatchEndForward(
                new CodeMatch(OpCodes.Ldarg_0),
                new CodeMatch(OpCodes.Ldc_R4),
                new CodeMatch(ci =>
                    ci.StoresField(AccessTools.Field(typeof(InWorldContainer), "temperatureCached"))))
            .ThrowIfInvalid($"[Mad Foundation] Could not locate ending signature in {original.DeclaringType}.{original.Name}");

        // Call onRequireSyncToClient at the end if any slots are dirty.
        matcher.InsertAfter(
            new CodeInstruction(OpCodes.Ldarg_0),
            new CodeInstruction(OpCodes.Call,
                AccessTools.Method(typeof(InWorldContainerTransitionFix), nameof(SyncToClientIfDirty))));
        
        return matcher.Instructions();
    }
}