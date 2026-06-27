using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

// ReSharper disable once CheckNamespace
namespace MadFoundation;

// World interactions are processed by both the client and the server at independent rates. If the server finishes an interaction first,
// and that interaction results in a block change (e.g. processing the last item in a ground storage), and the server block change packet
// sent to the client gets processed _before_ the client finishes its own interaction processing, then the client sends the cancel task
// to the new block and not the original. This leads to desyncs, stuck animations, and other issues.

// There are two "easy" fixes we can try. One is to check when the block change packet is received from the server if a world interaction
// is in progress on that block. We can then call the cancel method before replacing the block. There are 2 places of note here:

// GeneralPacketHandler.HandleSetBlock - Runs on main thread.
// SystemNetworkProcess.ProcessInBackground - Runs in background thread, has 3 closures that are queued for main thread execution.

// The relevant packet ID's are 7, 47, 63, and 70. See ServerSystemBlockSimulation.HandleDirtyAndUpdatedBlocks for details.

// The first one seems to be the main one to worry about. The rest seem to be special cases for large updates, chunk loading, no light
// updates, world map, et al.

// The second place to try to fix this is to prefix all implementations and overloads of IBlockAccessor.SetBlock. This would require some
// state tracking. E.g. we don't want to run logic if we're currently inside world interaction processing and the interaction itself
// changed a block... probably. So we'd need to flag the start and end of that, not too bad.

// There's also IBlockAccessor.ExchangeBlock. That might need consideration? It's unclear. Its goal is to change the block without
// touching the block entity at the location. It's very situational.

// Let's try patching just GeneralPacketHandler.HandleSetBlock first.

[MadPatch]
public class InteractionServerRaceFix
{
    public static void RegisterPatches(ICoreAPI api, Harmony harmony)
    {
        harmony.Patch(
            AccessTools.Method(typeof(GeneralPacketHandler), "HandleSetBlock"),
            transpiler: new HarmonyMethod(AccessTools.Method(typeof(InteractionServerRaceFix),
                nameof(HandleSetBlockTranspiler))));
    }

    private static void CheckAndCancelWorldinteractionsAt(ClientMain game, BlockPos pos)
    {
        var controls = game.EntityPlayer.Controls;
        if (controls.HandUse != EnumHandInteract.BlockInteract || !pos.Equals(controls.HandUsingBlockSel.Position)) return;

        var dt = (game.ElapsedMilliseconds - controls.UsingBeginMS) / 1000f;
        var block = game.BlockAccessor.GetBlock(controls.HandUsingBlockSel.Position);
        
        // There's also a block field in HandUsingBlockSel, but the game never access it, so I won't here.
        controls.HandUse = block.OnBlockInteractCancel(
            dt, game, game.player, controls.HandUsingBlockSel, EnumItemUseCancelReason.MovedAway
            ) ? EnumHandInteract.None : EnumHandInteract.BlockInteract;
        
        game.SendHandInteraction(
            2, controls.HandUsingBlockSel, null, EnumHandInteract.BlockInteract,
            EnumHandInteractNw.CancelBlockUse, false, EnumItemUseCancelReason.MovedAway
            );
    }
    
    private static IEnumerable<CodeInstruction> HandleSetBlockTranspiler(
        IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        var matcher = new CodeMatcher(instructions);
        var matches = 0;

        // Target uses a couple of overloads, find them both.
        while (matcher.MatchStartForward(
                   new CodeMatch(ci => (ci.opcode == OpCodes.Call || ci.opcode == OpCodes.Callvirt) &&
                                       ci.operand is MethodBase { Name: "SetBlock" } method &&
                                       method.DeclaringType == typeof(IBlockAccessor))
               ).IsValid)
        {
            matches++;
            var matchedInstruction = matcher.Instruction;

            matcher.MatchStartBackwards(new CodeMatch(ci => ci.opcode == OpCodes.Ldarg_0))
                .ThrowIfInvalid($"[Mad Foundation] Could not locate start of SetBlock in {original.DeclaringType}.{original.Name}");

            matcher.Insert(
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(
                    typeof(GeneralPacketHandler), "game")),
                new CodeInstruction(OpCodes.Ldloc_0),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(
                    typeof(InteractionServerRaceFix),
                    nameof(CheckAndCancelWorldinteractionsAt)))
            ).ThrowIfInvalid($"[Mad Foundation] Failed inserting instructions patching {original.DeclaringType}.{original.Name}");

            matcher.Start().MatchStartForward(new CodeMatch(matchedInstruction))
                .ThrowIfInvalid($"[Mad Foundation] Lost our place patching {original.DeclaringType}.{original.Name}")
                .Advance();
        }
        
        switch (matches)
        {
            case 0: throw new InvalidOperationException(
                $"[Mad Foundation] Could not locate calls to SetBlock in {original.DeclaringType}.{original.Name}");
            case not 2: throw new InvalidOperationException(
                $"[Mad Foundation] Expected 2 calls to SetBlock in {original.DeclaringType}.{original.Name}, found {matches}");
        }
        
        return matcher.Instructions();
    }
}