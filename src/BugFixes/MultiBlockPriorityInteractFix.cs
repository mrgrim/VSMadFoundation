using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.Client.NoObf;
using Vintagestory.GameContent;

namespace MadFoundation;

// The vanilla game does not extend the PriorityInteract flag to Multiblock proxies. See:
// https://github.com/anegostudios/VintageStory-Issues/issues/9199 -
//   "PlacedPriorityInteract not respected when aimed at the multiblock part."

// This fix works by intercepting all overloaded field loads of the PlacedPriorityInteract field in the mouse interaction handling code
// and replacing them with the method below to special case Multiblocks.

[MadPatch]
public class MultiBlockPriorityInteractFix
{
    public static void RegisterPatches(ICoreAPI api, Harmony harmony)
    {
        harmony.Patch(
            AccessTools.Method(typeof(SystemMouseInWorldInteractions), "HandleMouseInteractionsBlockSelected"),
            transpiler: new HarmonyMethod(AccessTools.Method(typeof(MultiBlockPriorityInteractFix),
                nameof(HandlePlacedPriorityInteractLoadFieldTranspiler))));
    }

    private static bool GetPlacedPriorityInteractWithMultiBlockHandling(Block block, BlockSelection blockSelection, ClientMain game)
    {
        if (block is not BlockMultiblock multiBlock) return block.PlacedPriorityInteract;
        
        var controllerPos = multiBlock.GetControlBlockPos(blockSelection.Position);
        var controllerBlock = game.WorldMap.RelaxedBlockAccess.GetBlock(controllerPos);
        return controllerBlock?.PlacedPriorityInteract ?? block.PlacedPriorityInteract;
    }
    
    private static IEnumerable<CodeInstruction> HandlePlacedPriorityInteractLoadFieldTranspiler(
        IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        var matcher = new CodeMatcher(instructions);

        // Find all loads of this field.
        while (matcher.MatchStartForward(
                   new CodeMatch(ci => ci.LoadsField(AccessTools.Field(typeof(Block), nameof(Block.PlacedPriorityInteract))))
               ).IsValid)
        {
            matcher.SetAndAdvance(OpCodes.Ldloc_0, null);
            matcher.InsertAndAdvance(
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(SystemMouseInWorldInteractions), "game")),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(
                    typeof(MultiBlockPriorityInteractFix),
                    nameof(GetPlacedPriorityInteractWithMultiBlockHandling)))
            ).ThrowIfInvalid($"[Mad Foundation] Failed replacing Block.PlacedPriorityInteract field load in {original.DeclaringType}.{original.Name}");
        }
        
        return matcher.Instructions();
    }
}