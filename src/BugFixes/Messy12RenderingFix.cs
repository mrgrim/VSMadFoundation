using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace MadFoundation;

// If a Messy12 ground storage ends up with more than 12 items the default renderer will overlap all items past 12 on top of each other
// with concentric rotations. This can get messy and computationally expensive quickly. It already accounts for this situation in the
// transform array lookup. This just patches it to not even render meshes past 12 items.

[MadPatch]
public class Messy12RenderingFix
{
    public static void RegisterPatches(ICoreAPI api, Harmony harmony)
    {
        harmony.Patch(
            AccessTools.Method(typeof(BlockEntityGroundStorage), "getOrCreateMesh"),
            transpiler: new HarmonyMethod(AccessTools.Method(typeof(Messy12RenderingFix),
                nameof(GetOrCreateMeshTranspiler))));
    }
    
    public static int CapMessy12MeshCount(ItemStack __instance) => Math.Min(__instance.StackSize, 12);

    private static IEnumerable<CodeInstruction> GetOrCreateMeshTranspiler(
        IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        var matcher = new CodeMatcher(instructions);

        matcher.MatchStartForward(
                new CodeMatch(ci => ci.opcode == OpCodes.Ldfld &&
                                    ci.operand is FieldInfo { Name: "Messy12Transforms" } fi &&
                                    fi.FieldType == typeof(ModelTransform[])))
            .ThrowIfInvalid($"[Mad Foundation] Could not locate Messy12Transforms field load in {original.DeclaringType}.{original.Name}");
        
        matcher.MatchStartForward(
                new CodeMatch(ci => ci.Calls(AccessTools.PropertyGetter(typeof(ItemStack), nameof(ItemStack.StackSize)))))
            .ThrowIfInvalid($"[Mad Foundation] Could not locate ItemStack.StackSize getter in {original.DeclaringType}.{original.Name}");

        matcher.Set(OpCodes.Call, AccessTools.Method(typeof(Messy12RenderingFix), nameof(CapMessy12MeshCount)));
        
        return matcher.InstructionEnumeration();
    }
}