using System.Text;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace MadFoundation;

// That was easy...

[MadPatch]
public class GroundStorageTransitionInfo
{
    public static void RegisterPatches(ICoreAPI api, Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(BlockEntityGroundStorage), nameof(BlockEntityGroundStorage.GetBlockInfo)),
            postfix: AccessTools.Method(typeof(GroundStorageTransitionInfo), nameof(BlockInfoPostfix)));
    }
    
    public static void BlockInfoPostfix(BlockEntityGroundStorage __instance, StringBuilder dsc)
    {
        var slots = __instance.Inventory
            .Select(slot => slot)
            .Where(slot => slot?.Itemstack?.Collectible.TransitionableProps?.Length > 0);
        
        foreach (var slot in slots)
            slot.Itemstack?.Collectible.AppendPerishableInfoText(slot, dsc, __instance.Api.World);
    }
}