using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace MadFoundation;

// This rewrites the start of BlockEntityGroundStorage.OnPlayerInteractStart to allow scanning for multiple instances of
// collectible behaviors implementing IContainedInteractable and trying to interact with each in turn until one succeeds.
// The original behavior is to only look for one instance, but in the case of ground storage the interaction is often
// locked by requirements such as a specific tool being held, so there's no reason not to allow multiple copies of the
// behavior.

// It alters the BlockGroundStorage.GetPlacedBlockInteractionHelp method to show all the possible interactions.

// The enormous method for displaying various interactions in the handbook is patched to show multiple ground storage processable
// behaviors.

[MadPatch]
public class GroundStorageMultipleInteractions
{
    private static readonly ConditionalWeakTable<BlockEntityGroundStorage, IContainedInteractable> activeInteractions = new();
    
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "isUsingSlot")]
    private static extern ref ItemSlot? isUsingSlot(BlockEntityGroundStorage _);

    public static void RegisterPatches(ICoreAPI api, Harmony harmony)
    {
        harmony.Patch(
            AccessTools.Method(typeof(BlockEntityGroundStorage), nameof(BlockEntityGroundStorage.OnPlayerInteractStart)),
            transpiler: new HarmonyMethod(AccessTools.Method(typeof(GroundStorageMultipleInteractions),
                nameof(OnPlayerInteractStartTranspiler))));
        harmony.Patch(
            AccessTools.Method(typeof(BlockEntityGroundStorage), nameof(BlockEntityGroundStorage.OnPlayerInteractStep)),
            prefix: new HarmonyMethod(AccessTools.Method(typeof(GroundStorageMultipleInteractions),
                nameof(OnPlayerInteractStepPrefix))));
        harmony.Patch(
            AccessTools.Method(typeof(BlockEntityGroundStorage), nameof(BlockEntityGroundStorage.OnPlayerInteractStop)),
            prefix: new HarmonyMethod(AccessTools.Method(typeof(GroundStorageMultipleInteractions),
                nameof(OnPlayerInteractStopPrefix))));
        harmony.Patch(
            AccessTools.Method(typeof(BlockEntityGroundStorage), nameof(BlockEntityGroundStorage.OnPlayerInteractCancel)),
            prefix: new HarmonyMethod(AccessTools.Method(typeof(GroundStorageMultipleInteractions),
                nameof(OnPlayerInteractCancelPrefix))));
        
        harmony.Patch(
            AccessTools.Method(typeof(BlockGroundStorage), nameof(BlockGroundStorage.GetPlacedBlockInteractionHelp)),
            transpiler: new HarmonyMethod(AccessTools.Method(typeof(GroundStorageMultipleInteractions),
                nameof(ContainedInteractionsTranspiler))));

        // More Piles reverse patches this entire method verbatim then wraps it in a cancelling prefix that eats exceptions. The
        // stated reason is to deal with a "vanilla bug", but no additional detail is provided.
        // See: https://github.com/Craluminum-Mods/CraluminumMods-Issues/issues/99
        if (api.ModLoader.IsModEnabled("morepiles"))
        {
            new ReversePatcher(harmony,
                typeof(BlockGroundStorage).GetMethod(nameof(BlockGroundStorage.GetPlacedBlockInteractionHelp)),
                new HarmonyMethod(MorePilesCompat.Base)).Patch(HarmonyReversePatchType.Snapshot);

            harmony.Patch(
                AccessTools.Method(typeof(BlockGroundStorage), nameof(BlockGroundStorage.GetPlacedBlockInteractionHelp)),
                prefix: new HarmonyMethod(MorePilesCompat.Prefix, -1, ["craluminum2413.morepiles"]));
        }
    }

    public static void RegisterLatePatches(ICoreAPI api, Harmony harmony)
    {
        // The version of Harmony shipped with VS seems to have a bug where if nothing has altered the original method then requesting
        // a snapshot reverse patch will throw a null argument exception.
        var addProcessIntoInfoOrig = AccessTools.Method(typeof(CollectibleBehaviorHandbookTextAndExtraInfo), "addProcessesIntoInfo");
        new ReversePatcher(harmony, addProcessIntoInfoOrig,
            new HarmonyMethod(HandbookProcessesIntoInfoSlice)).Patch(
            Harmony.GetPatchInfo(addProcessIntoInfoOrig) is null ? HarmonyReversePatchType.Original : HarmonyReversePatchType.Snapshot);
        harmony.Patch(addProcessIntoInfoOrig,
            transpiler: new HarmonyMethod(AccessTools.Method(typeof(GroundStorageMultipleInteractions),
                nameof(HandbookProcessesIntoInfoTranspiler))));
    }

    private static bool PlayerInteractStartExtension(
        BlockEntityGroundStorage storage,
        ItemSlot slotAt,
        IPlayer player,
        BlockSelection bs)
    {
        if (activeInteractions.Remove(storage, out var activeCollectibleInterface))
            activeCollectibleInterface.OnContainedInteractCancel(
                0.0f, storage, slotAt, player, bs, EnumItemUseCancelReason.ChangeSlot);

        if (slotAt.Itemstack?.Collectible is null) return false;
        var collectibleInterfaces = GetCollectibleInterfaces<IContainedInteractable>(slotAt.Itemstack.Collectible);

        foreach (var collectibleInterface in collectibleInterfaces)
        {
            if (!collectibleInterface.OnContainedInteractStart(storage, slotAt, player, bs)) continue; 

            // Vanilla ground storage pauses all interaction handling after each interaction until the right mouse button
            // is released. Allow this behavior to be overridden by the item being stored with an attribute.
            
            // This could possibly be handled as a behavior, or even better if items ever get tags, or perhaps an unused storage flag?
            // Storage flag would be perfect, but there's no central coordination for them. :(
            
            // https://apidocs.vintagestory.at/api/Vintagestory.API.Common.EnumItemStorageFlags.html
            if (slotAt.Itemstack.ItemAttributes["madInteractionPause"].AsBool(true))
                BlockGroundStorage.IsUsingContainedBlock = true;
            
            activeInteractions.Add(storage, collectibleInterface);
            isUsingSlot(storage) = slotAt;
            
            return true;
        }

        return false;
    }
    
    // We're replacing the if at the start of the method
    // https://github.com/anegostudios/vssurvivalmod/blob/36d9550e19c32197e8f0d8a9780a5d0b1320dc7c/BlockEntity/BEGroundStorage.cs#L461
    private static IEnumerable<CodeInstruction> OnPlayerInteractStartTranspiler(
        IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        var matcher = new CodeMatcher(instructions);
        
        matcher.MatchStartForward(
                new CodeMatch(ci => ci.IsLdloc()),
                new CodeMatch(ci => ci.operand is MethodInfo { Name: "get_Itemstack" }),
                new CodeMatch(ci => ci.operand is MethodInfo { Name: "get_Collectible" }),
                new CodeMatch(ci => ci.operand is MethodInfo { Name: "GetCollectibleInterface", IsGenericMethod: true } mi && 
                                    mi.GetGenericArguments()[0] == typeof(IContainedInteractable)))
            .ThrowIfInvalid($"[Mad Foundation] Could not locate starting signature in {original.DeclaringType}.{original.Name}");

        var startPos = matcher.Pos;
        var slotAtLoad = matcher.Instruction.Clone();
        
        matcher.MatchStartForward(
                new CodeMatch(ci => ci.opcode == OpCodes.Stfld &&
                                    ci.operand is FieldInfo { Name: "isUsingSlot" } fi &&
                                    fi.FieldType == typeof(ItemSlot)),
                new CodeMatch(ci => ci.opcode == OpCodes.Ldc_I4_1),
                new CodeMatch(ci => ci.opcode == OpCodes.Ret))
            .ThrowIfInvalid($"[Mad Foundation] Could not locate ending signature in {original.DeclaringType}.{original.Name}");
        
        var endPos = matcher.Pos;
        var skipRetLabel = matcher.InstructionAt(3).labels.FirstOrDefault();
        
        matcher.Start().Advance(startPos);
        var removeCount = endPos - startPos + 1; // Inclusive
        matcher.RemoveInstructions(removeCount);
        
        var pushSlotAt     = new CodeInstruction(slotAtLoad.opcode, slotAtLoad.operand);

        matcher.Insert(
            new CodeInstruction(OpCodes.Ldarg_0),
            pushSlotAt,
            new CodeInstruction(OpCodes.Ldarg_1),
            new CodeInstruction(OpCodes.Ldarg_2),
            new CodeInstruction(OpCodes.Call, AccessTools.Method(
                typeof(GroundStorageMultipleInteractions),
                nameof(PlayerInteractStartExtension))),
            new CodeInstruction(OpCodes.Brfalse, skipRetLabel)
        );

        return matcher.Instructions();
    }

    // The point of these next 3 prefixes is to call the right behavior instance method. The vanilla ones only call the first found.
    private static bool OnPlayerInteractStepPrefix(
        BlockEntityGroundStorage __instance,
        // ReSharper disable once RedundantAssignment
        ref bool __result,
        float secondsUsed,
        IPlayer byPlayer,
        BlockSelection blockSel)
    {
        __result = false;

        if (activeInteractions.TryGetValue(__instance, out var activeCollectibleInterface))
            __result = activeCollectibleInterface.OnContainedInteractStep(secondsUsed, __instance, isUsingSlot(__instance), byPlayer,
                blockSel);
        else        
            isUsingSlot(__instance) = null;
        
        return false;
    }
    
    private static bool OnPlayerInteractStopPrefix(
        BlockEntityGroundStorage __instance,
        float secondsUsed,
        IPlayer byPlayer,
        BlockSelection blockSel)
    {
        if (activeInteractions.Remove(__instance, out var activeCollectibleInterface))
            activeCollectibleInterface.OnContainedInteractStop(secondsUsed, __instance, isUsingSlot(__instance), byPlayer, blockSel);

        isUsingSlot(__instance) = null;
        return false;
    }

    // Really curious what the use case for a cancel returning false here is.
    private static bool OnPlayerInteractCancelPrefix(
        BlockEntityGroundStorage __instance,
        // ReSharper disable once RedundantAssignment
        ref bool __result,
        float secondsUsed,
        IPlayer byPlayer,
        BlockSelection blockSel,
        EnumItemUseCancelReason cancelReason)
    {
        __result = true;

        if (activeInteractions.Remove(__instance, out var activeCollectibleInterface))
            __result = activeCollectibleInterface.OnContainedInteractCancel(
                secondsUsed, __instance, isUsingSlot(__instance), byPlayer, blockSel, cancelReason);

        isUsingSlot(__instance) = null;
        return false;
    }
    
    public static List<T> GetCollectibleInterfaces<T>(CollectibleObject collectible) where T : class
    {
        List<T> ret = [];
        
        if (collectible is T collectibleInterface)
            ret.Add(collectibleInterface);

        ret.AddRange(collectible.CollectibleBehaviors.Select(t => t as T ?? null).OfType<T>());

        return ret;
    }

    public static WorldInteraction[] GetAllContainedInteractionsHelp(
        ItemSlot? slotAt,
        BlockEntityContainer groundStorageBlock,
        BlockSelection selection,
        IPlayer forPlayer)
    {
        List<WorldInteraction> interactions = [];

        if (slotAt?.Itemstack?.Collectible is null) return interactions.ToArray();
        var collectibleInterfaces = GetCollectibleInterfaces<IContainedInteractable>(slotAt.Itemstack.Collectible);
        
        foreach (var interactable in collectibleInterfaces)
            interactions.AddRange(interactable.GetContainedInteractionHelp(groundStorageBlock, slotAt, forPlayer, selection)
                                  ?? ReturnEmptyAndComplain(interactable));
        
        return interactions.ToArray();
        
        WorldInteraction[] ReturnEmptyAndComplain(IContainedInteractable interactable)
        {
            MadFoundationModSystem._api?.Logger.Warning($"[Mad Foundation] A contained interactable class ({interactable.GetType().FullName}) " +
                                                        $"returned null when calling \"GetContainedInteractableHelp\". This is a bug. Please " +
                                                        $"report this.");
            return [];
        }
    }
    
    // We're replacing the call to the first IContainedInteractable.GetContainedInteractionHelp with a method to combine the results
    // from every IContainedInteractable.GetContainedInteractionHelp. Basically, this line:
    // https://github.com/anegostudios/vssurvivalmod/blob/36d9550e19c32197e8f0d8a9780a5d0b1320dc7c/Block/BlockGroundStorage.cs#L455
    public static IEnumerable<CodeInstruction> ContainedInteractionsTranspiler(
        IEnumerable<CodeInstruction> instructions,
        MethodBase original)
    {
        var anchorMethod = AccessTools.Method(
            typeof(IContainedInteractable),
            nameof(IContainedInteractable.GetContainedInteractionHelp));
        var replacement = AccessTools.Method(
            typeof(GroundStorageMultipleInteractions),
            nameof(GetAllContainedInteractionsHelp));
        var getSlotAt = AccessTools.Method(
            typeof(BlockEntityGroundStorage),
            nameof(BlockEntityGroundStorage.GetSlotAt));
        var arrayEmptyOpen = typeof(Array).GetMethod(nameof(Array.Empty), BindingFlags.Public | BindingFlags.Static);

        var matcher = new CodeMatcher(instructions);

        matcher.MatchStartForward(new CodeMatch(ci => ci.Calls(anchorMethod)))
               .ThrowIfInvalid($"[Mad Foundation] Could not locate call to {nameof(IContainedInteractable)}.{nameof(IContainedInteractable.GetContainedInteractionHelp)} in {original.DeclaringType}.{original.Name}");
        var anchorPos = matcher.Pos;

        matcher.Start().Advance(anchorPos);
        matcher.MatchStartBackwards(new CodeMatch(ci => ci.Calls(getSlotAt)))
            .ThrowIfInvalid($"[Mad Foundation] Could not locate `call BlockEntityGroundStorage.GetSlotAt` pattern preceding GetContainedInteractionHelp in {original.DeclaringType}.{original.Name}");
        var startPos = matcher.Advance(2).Pos;

        matcher.Start().Advance(anchorPos);
        var blockEntityLoad = matcher.InstructionAt(-4).Clone();
        var slotAtLoad      = matcher.InstructionAt(-3).Clone();
        var forPlayerLoad   = matcher.InstructionAt(-2).Clone();
        var selectionLoad   = matcher.InstructionAt(-1).Clone();

        matcher.Start().Advance(anchorPos);
        matcher.MatchStartForward(new CodeMatch(ci => ci.opcode == OpCodes.Call
                                                      && ci.operand is MethodInfo { IsGenericMethod: true } mi
                                                      && mi.GetGenericMethodDefinition() == arrayEmptyOpen
                                                      && mi.GetGenericArguments()[0] == typeof(WorldInteraction)))
            .ThrowIfInvalid($"[Mad Foundation] Could not locate trailing Array.Empty<WorldInteraction>() in {original.DeclaringType}.{original.Name}");
        var endPos = matcher.Pos; // inclusive end of the span to remove

        var afterEnd = matcher.InstructionAt(1);
        if (!afterEnd.IsStloc())
            throw new InvalidOperationException(
                $"[Mad Foundation] Expected stloc directly after Array.Empty<WorldInteraction>(); got {afterEnd.opcode}.");

        // Preserve any labels and exception blocks attached to the first removed instruction so jumps
        // into the chain still resolve to the replacement.
        matcher.Start().Advance(startPos);
        var leadingLabels = new List<Label>(matcher.Instruction.labels);
        var leadingBlocks = new List<ExceptionBlock>(matcher.Instruction.blocks);

        var removeCount = endPos - startPos + 1;
        matcher.RemoveInstructions(removeCount);

        var pushSlotAt     = new CodeInstruction(slotAtLoad.opcode, slotAtLoad.operand);           // ItemSlot
        var pushBlockEnt   = new CodeInstruction(blockEntityLoad.opcode, blockEntityLoad.operand); // BlockEntityContainer
        var pushSelection  = new CodeInstruction(selectionLoad.opcode, selectionLoad.operand);     // BlockSelection
        var pushForPlayer  = new CodeInstruction(forPlayerLoad.opcode, forPlayerLoad.operand);     // IPlayer

        pushSlotAt.labels.AddRange(leadingLabels);
        foreach (var b in leadingBlocks) pushSlotAt.blocks.Add(b);

        matcher.Insert(
            pushSlotAt,
            pushBlockEnt,
            pushSelection,
            pushForPlayer,
            new CodeInstruction(OpCodes.Call, replacement));

        return matcher.Instructions();
    }
    // This is a bit nuts, but hopefully very compatible. This is a late patch that will attempt to capture the handbook logic for ground
    // storage processing via a reverse proxy including any other mods transpiled changes. Then we delete the entire block from
    // CollectibleBehaviorHandbookTextAndExtraInfo.addProcessesIntoInfo and replace it with a call to the below method which loops over
    // all attached behaviors and calls the extracted logic for each.
    
    // A couple of strategies of note. The entire block is identified by the jump target of the negative result of the if statement
    // checking for the behavior, which should make it very resilient. This method is also gigantic, and it initializes a lambda
    // capture class at the start and stores it in local index 0. We need to pass this through as it captures the capi argument and is
    // referenced heavily in the reverse patched method.
    public static void HandbookHandleMultipleGroundStoredProcessable(
        CollectibleBehaviorHandbookTextAndExtraInfo __instance,
        ItemStack stack,
        List<RichTextComponentBase> components,
        float marginBottom,
        bool haveText,
        object lambdaCaptureClass)
    {
        var behaviors =
            GetCollectibleInterfaces<CollectibleBehaviorGroundStoredProcessable>(stack.Collectible);
        
        foreach (var behavior in behaviors)
            HandbookProcessesIntoInfoSlice(__instance, null, null, stack, components,
                null, marginBottom, null, null, haveText, behavior, lambdaCaptureClass);
    }
    
    public static IEnumerable<CodeInstruction> HandbookProcessesIntoInfoTranspiler(
        IEnumerable<CodeInstruction> instructions,
        MethodBase original)
    {
        var matcher = new CodeMatcher(instructions);

        matcher.MatchStartForward(
                new CodeMatch(OpCodes.Ldarg_3), 
                new CodeMatch(ci => ci.opcode == OpCodes.Callvirt && ci.operand is MethodInfo { Name: "get_Collectible" }),
                new CodeMatch(OpCodes.Ldc_I4_1),
                new CodeMatch(ci => ci.opcode == OpCodes.Callvirt
                                    && ci.operand is MethodInfo { IsGenericMethod: true } mi
                                    && mi.GetGenericMethodDefinition() == AccessTools.Method(
                                        typeof(CollectibleObject), nameof(CollectibleObject.GetCollectibleBehavior), [typeof(bool)])
                                    && mi.GetGenericArguments()[0] == typeof(CollectibleBehaviorGroundStoredProcessable)))
            .ThrowIfInvalid($"[Mad Foundation] Cannot find starting signature in {original.Name} transpiler");

        if (matcher.Instruction.labels.Count == 0) throw new InvalidOperationException(
            $"[Mad Foundation] Invalid start label found in {original.Name} transpiler");
        var startLabels = matcher.Instruction.ExtractLabels();

        var startPos = matcher.Pos;
        matcher.MatchStartForward(new CodeMatch(OpCodes.Brfalse));

        if (matcher.Instruction.operand is not Label endLabel) throw new InvalidOperationException(
            $"[Mad Foundation] Invalid end label searching for local store in {original.Name} transpiler ");

        matcher.MatchStartForward(new CodeMatch(ci => ci.labels.Contains(endLabel)))
            .ThrowIfInvalid($"[Mad Foundation] Cannot find end label in {original.Name} transpiler");
        matcher.Advance(-1);
        var endPos = matcher.Pos;

        matcher.RemoveInstructionsInRange(startPos, endPos);
        matcher.Start().Advance(startPos);
        
        var itemStackArgIndex = original.GetParameters().Last(p =>
            p.ParameterType == typeof(ItemStack) && p.Name == "stack").Position + 1;
        var componentsArgIndex = original.GetParameters().Last(p =>
            p.ParameterType == typeof(List<RichTextComponentBase>) && p.Name == "components").Position + 1;
        var marginBottomArgIndex = original.GetParameters().Last(p =>
            p.ParameterType == typeof(float) && p.Name == "marginBottom").Position + 1;
        var haveTextArgIndex = original.GetParameters().Last(p =>
            p.ParameterType == typeof(bool) && p.Name == "haveText").Position + 1;

        matcher.Insert(
            new CodeInstruction(OpCodes.Ldarg_0).WithLabels(startLabels), // this
            new CodeInstruction(OpCodes.Ldarg, itemStackArgIndex),
            new CodeInstruction(OpCodes.Ldarg, componentsArgIndex),
            new CodeInstruction(OpCodes.Ldarg, marginBottomArgIndex),
            new CodeInstruction(OpCodes.Ldarg, haveTextArgIndex),
            new CodeInstruction(OpCodes.Ldloc_0),
            new CodeInstruction(OpCodes.Call, AccessTools.Method(
                typeof(GroundStorageMultipleInteractions), nameof(HandbookHandleMultipleGroundStoredProcessable))));

        return matcher.Instructions();
    }
    
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void HandbookProcessesIntoInfoSlice(
        CollectibleBehaviorHandbookTextAndExtraInfo __instance,
        ICoreClientAPI? capi,
        ActionConsumable<string>? openDetailPageFor,
        ItemStack stack,
        List<RichTextComponentBase> components,
        float? marginTop,
        float marginBottom,
        List<ItemStack>? containers,
        List<ItemStack>? fuels,
        bool haveText,
        CollectibleBehaviorGroundStoredProcessable injectedBehavior,
        object lambdaCaptureClass)
    {
        _ = Transpiler([], AccessTools.Method(typeof(GroundStorageMultipleInteractions), nameof(HandbookProcessesIntoInfoSlice)));
        return;

        IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            var injectedBehaviorIndex = AccessTools.Method(typeof(GroundStorageMultipleInteractions), nameof(HandbookProcessesIntoInfoSlice))
                .GetParameters().Last(p => p.ParameterType == typeof(CollectibleBehaviorGroundStoredProcessable)).Position;
            var lambdaCaptureClassIndex = AccessTools.Method(typeof(GroundStorageMultipleInteractions), nameof(HandbookProcessesIntoInfoSlice))
                .GetParameters().Last(p => p.ParameterType == typeof(object) && p.Name == "lambdaCaptureClass").Position;
            
            var matcher = new CodeMatcher(instructions);

            matcher.MatchStartForward(
                new CodeMatch(OpCodes.Ldarg_3), 
                new CodeMatch(ci => ci.opcode == OpCodes.Callvirt && ci.operand is MethodInfo { Name: "get_Collectible" }),
                new CodeMatch(OpCodes.Ldc_I4_1),
                new CodeMatch(ci => ci.opcode == OpCodes.Callvirt
                                    && ci.operand is MethodInfo { IsGenericMethod: true } mi
                                    && mi.GetGenericMethodDefinition() == AccessTools.Method(
                                        typeof(CollectibleObject), nameof(CollectibleObject.GetCollectibleBehavior), [typeof(bool)])
                                    && mi.GetGenericArguments()[0] == typeof(CollectibleBehaviorGroundStoredProcessable)))
                .ThrowIfInvalid($"[Mad Foundation] Cannot find starting signature while reverse patching {original.Name}");

            matcher.MatchStartForward(new CodeMatch(OpCodes.Brfalse));

            if (matcher.Instruction.operand is not Label endLabel) throw new InvalidOperationException(
                    $"[Mad Foundation] Invalid end label searching for local store while reverse patching {original.Name}");

            matcher.Advance();

            var localStore = matcher.InstructionAt(-3).Clone();
            if (localStore.opcode != OpCodes.Stloc_S) throw new InvalidOperationException(
                    $"[Mad Foundation] Invalid opcode searching for local store while reverse patching {original.Name}");
            
            matcher.Insert(
                new CodeInstruction(OpCodes.Ldarg, injectedBehaviorIndex),
                localStore,
                new CodeInstruction(OpCodes.Ldarg, lambdaCaptureClassIndex),
                new CodeInstruction(OpCodes.Stloc_0));
            
            var startPos = matcher.Pos;
            
            matcher.MatchEndForward(new CodeMatch(ci => ci.labels.Contains(endLabel)))
                .ThrowIfInvalid($"[Mad Foundation] Cannot find end label while reverse patching {original.Name}");

            var retInst = new CodeInstruction(OpCodes.Ret);
            retInst.MoveLabelsFrom(matcher.Instruction);
            matcher.Insert(retInst);
            
            var endPos = matcher.Pos;
            
            var newInstructions = matcher.Instructions().GetRange(startPos, endPos - startPos + 1);

            return newInstructions;
        }
    }
}

public static class MorePilesCompat
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static WorldInteraction[] Base(this BlockGroundStorage instance, IWorldAccessor world,
        BlockSelection selection, IPlayer forPlayer) => [];

    public static bool Prefix(BlockGroundStorage __instance, ref WorldInteraction[] __result, IWorldAccessor world, BlockSelection selection, IPlayer forPlayer)
    {
        try { __result = Base(__instance, world, selection, forPlayer); }
        catch { __result = []; }

        return false;
    }
}
