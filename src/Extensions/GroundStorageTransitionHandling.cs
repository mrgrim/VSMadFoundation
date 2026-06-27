using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace MadFoundation;

// When a transition occurs on a ground storage and the result has a different ground storage type than the original, the
// client does not correct for this. Vanilla has very specific exclusions carved out where this can happen. For example, when
// completing a clay form of multiple items or when a pit kiln finishes. This has never been generalized though.

// This patch registers a listener on the inventory for when a slot changes, and if the storage properties reference has changed
// it uses the same mechanism as those exceptions to force the ground storage into a new type.

[MadPatch]
public class GroundStorageTransitionHandling
{
    public static void RegisterPatches(ICoreAPI api, Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(BlockEntityGroundStorage), nameof(BlockEntityGroundStorage.Initialize)),
            postfix: AccessTools.Method(typeof(GroundStorageTransitionHandling), nameof(InitializePostfix)));
        harmony.Patch(AccessTools.Method(typeof(BlockEntityGroundStorage), nameof(BlockEntityGroundStorage.OnBlockRemoved)),
            prefix: AccessTools.Method(typeof(GroundStorageTransitionHandling), nameof(OnBlockRemovedPrefix)));
    }
    
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_StorageProps")]
    public static extern void SetStorageProps(BlockEntityGroundStorage instance, GroundStorageProperties? value);

    public static void HandleSlotModifiedStatic(BlockEntityGroundStorage instance, int slotId)
    {
        var itemStack = instance.Inventory.FirstNonEmptySlot?.Itemstack;
        var storageProps = itemStack?.Collectible?.GetBehavior<CollectibleBehaviorGroundStorable>()?.StorageProps;

        if (storageProps == instance.StorageProps || itemStack is null || instance.StorageProps is null) return;

        if (instance.StorageProps.Layout is not
            (EnumGroundStorageLayout.Messy12 or EnumGroundStorageLayout.Stacking or EnumGroundStorageLayout.SingleCenter)) return;

        SetStorageProps(instance, null);
        instance.DetermineStorageProperties(null);
        instance.forceStorageProps = true;
        instance.MarkDirty(true);
    }

    private static readonly MethodInfo SlotModifiedHandler =
        AccessTools.Method(typeof(GroundStorageTransitionHandling), nameof(HandleSlotModifiedStatic));

    public static void InitializePostfix(BlockEntityGroundStorage __instance)
    {
        if (__instance.Inventory == null) return;
        
        var dynamicDelegate = (Action<int>)Delegate.CreateDelegate(
            typeof(Action<int>),
            __instance,
            SlotModifiedHandler
        );

        __instance.Inventory.SlotModified += dynamicDelegate;
    }
    
    public static void OnBlockRemovedPrefix(BlockEntityGroundStorage __instance)
    {
        if (__instance.Inventory == null) return;
        
        var dynamicDelegate = (Action<int>)Delegate.CreateDelegate(
            typeof(Action<int>),
            __instance,
            SlotModifiedHandler
        );
        
        __instance.Inventory.SlotModified -= dynamicDelegate;
    }
}