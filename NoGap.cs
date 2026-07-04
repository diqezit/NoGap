using HarmonyLib;
using System;
using System.Collections.Generic;
using UnityEngine;

/*
 NoGap

 Vanilla terrain uses marching cubes and a per cell density sbyte value.
 Placing a normal building block next to terrain can leave that cell's
 density in a state where the terrain mesh visually pulls away from the
 block, showing a gap. A known manual fix closes this by editing only the
 density value of that one cell, without touching the block itself.

 Goal
 -----
 - Close the visual gap for blocks placed directly on or into terrain
 - Never touch blocks stacked in the air on top of other blocks
 - Never place, remove or replace any block - density only
 - Skip blocks whose visuals or behavior could break if density around
   them changes: doors, windows, growable plants, tree trunks, gore,
   spikes, trapdoors, ladders, bars, catwalks, railings, farm plots
 - Let the player manually exempt any block type at runtime, for cases
   the built in filters do not cover (modded blocks, edge cases)

 Why patch ChangeBlocks
 ----------------------
 GameManager.ChangeBlocks is the single point every block change passes
 through - placement, explosions, scripts - so patching it here catches
 every real placement without needing separate hooks per source.

 Why a ThreadStatic guard is required
 -------------------------------------
 SetBlocksRPC calls ChangeBlocks internally to apply the change on the
 server, which fires this same Postfix again. Without a guard the mod
 would try to fix its own density write, loop back into SetBlocksRPC,
 and repeat forever. The guard is set before SetBlocksRPC is called and
 cleared right after, so the re entrant call sees it set and returns.

 Why most block type filters have no shared BlockTag
 ------------------------------------------------------
 Doors, windows, growable plants, tree trunks, gore and spikes all set
 Block.BlockTag in blocks.xml, so a single BlockTag != None check
 excludes all of them. Trapdoors, ladders, bars, catwalks and railings
 do not carry a distinguishing BlockTag, so each is excluded by a
 different signal instead: C# type, the "Class" property, block name
 prefix, or FilterTags - whichever uniquely identifies that family.

 Why the manual toggle uses HitInfo.hit.blockValue
 -------------------------------------------------
 When a player holds a block and aims at the ground, HitInfo.lastBlockPos
 often points to the air space above the ground where the new block would
 be placed. HitInfo.hit.blockValue reliably holds the actual block
 currently highlighted by the crosshair, so we use it to ensure we toggle
 the intended block type.

 What the mod does in order
 ---------------------------
 1 Filter to real player placements
   bChangeBlockValue must be true, blockValue must not be air or a
   multiblock child, changedByEntityId must resolve to an EntityPlayer,
   and blockValue.isTerrain must be false - only non terrain blocks
   placed by a player are candidates.

 2 Skip excluded block types
   Manual toggle set, BlockTag, BlockTrapDoor, ladders, bars, catwalks,
   railings, windows and farm plots / fertile soil are all skipped here,
   see the filter rationale above.

 3 Confirm the block actually sits on terrain
   Scans straight down from the placed position up to ScanDistance
   cells. The first non air cell found decides the outcome: terrain
   applies the fix, a normal block stops the scan and skips the fix.
   This is what keeps blocks stacked in the air untouched.

 4 Apply the density fix
   Reads the cell's current density through WorldBase.GetDensity. If it
   already equals ConnectDensity nothing is queued. Otherwise a density
   only BlockChangeInfo is built for that cell. ConnectDensity is -120,
   matching the value the manual "/" gap fix uses - it sits between
   DensityAir and DensityTerrain, sealing the mesh without adding
   visible terrain mass. bForceDensityChange is required because
   ChangeBlocks otherwise clamps density on non terrain blocks and
   drops the change silently.

 5 Send as one batch
   All queued density changes are sent together through
   GameManager.SetBlocksRPC, attributed to the same persistentPlayerId
   that triggered the original change.

 Notes
 -----
 - Runs only when ConnectionManager.IsServer is true - clients never
   evaluate this logic, changes always arrive via RPC.
 - A HashSet<Vector3i> dedupes positions within one ChangeBlocks call,
   since a single batch can reference the same cell more than once.
 - _blocksToChange and GameManager.World are not null checked: Harmony
   postfixes only run after the original method returns without
   throwing, and ChangeBlocks itself depends on both being valid.

 Integration points (for future migration)
 -------------------------------------------
 GameManager.ChangeBlocks
 GameManager.SetBlocksRPC
 WorldBase.GetDensity(Vector3i)
 World.GetBlock(int,int,int)
 World.GetEntity(int)
 BlockChangeInfo(BlockValueRef, sbyte, bool)
 BlockValueRef.TryGetBlockPos
 BlockValue.isTerrain / isair / ischild
 BlockValue.Block
 Block.BlockTag / BlockTags enum
 Block.Properties / DynamicProperties.GetString
 Block.GetBlockName()
 Block.FilterTags
 Block.blockMaterial / MaterialBlock.FertileLevel
 BlockTrapDoor
 ConnectionManager.IsServer
 EntityPlayerLocal.HitInfo
*/

public sealed class NoGap : IModApi
{
    internal const string HarmonyId = "NoGap";

    public void InitMod(Mod modInstance)
    {
        new Harmony(HarmonyId).PatchAll();
    }
}

[HarmonyPatch(typeof(GameManager), nameof(GameManager.ChangeBlocks))]
public static class Patch_NoGap
{
    [ThreadStatic]
    private static bool guard;

    private const int ScanDistance = 6;
    private const sbyte ConnectDensity = -120;
    private const int FertileLevelThreshold = 16;
    private const string WindowFilterTag = "SC_windows";

    internal static readonly HashSet<string> DisabledBlocks = new HashSet<string>();

    private static readonly string[] SkippedBlockPrefixes =
    {
        "ironBars",
        "jailBars",
        "metalCatwalk",
        "metalRailing",
        "metalStairsBoardRailing"
    };

    private static void Postfix(
        GameManager __instance,
        PlatformUserIdentifierAbs persistentPlayerId,
        List<BlockChangeInfo> _blocksToChange)
    {
        if (guard)
            return;

        if (!SingletonMonoBehaviour<ConnectionManager>.Instance.IsServer)
            return;

        World world = __instance.World;

        guard = true;
        try
        {
            List<BlockChangeInfo> densityChanges = new List<BlockChangeInfo>();
            HashSet<Vector3i> seen = new HashSet<Vector3i>();

            for (int i = 0; i < _blocksToChange.Count; i++)
            {
                if (!TryGetPlacedBlock(world, _blocksToChange[i], out Vector3i pos, out int entityId))
                    continue;

                if (!seen.Add(pos))
                    continue;

                if (!HasTerrainBelow(world, pos))
                    continue;

                if (world.GetDensity(pos) == ConnectDensity)
                    continue;

                densityChanges.Add(
                    new BlockChangeInfo(new BlockValueRef(pos), ConnectDensity, true)
                    {
                        changedByEntityId = entityId
                    });
            }

            if (densityChanges.Count > 0)
                __instance.SetBlocksRPC(densityChanges, persistentPlayerId);
        }
        catch (Exception ex)
        {
            Log.Error("[NoGap] " + ex);
        }
        finally
        {
            guard = false;
        }
    }

    private static bool TryGetPlacedBlock(
        World world,
        BlockChangeInfo ch,
        out Vector3i pos,
        out int entityId)
    {
        pos = Vector3i.zero;
        entityId = -1;

        if (ch == null
            || !ch.bChangeBlockValue
            || ch.blockValue.isair
            || ch.blockValue.ischild)
            return false;

        entityId = ch.changedByEntityId;

        if (entityId <= 0
            || !ch.blockValueRef.TryGetBlockPos(out pos)
            || !(world.GetEntity(entityId) is EntityPlayer))
            return false;

        if (ch.blockValue.isTerrain)
            return false;

        Block placedBlock = ch.blockValue.Block;
        return placedBlock == null || !ShouldSkipBlock(placedBlock);
    }

    private static bool ShouldSkipBlock(Block block)
    {
        string blockName = block.GetBlockName();

        return DisabledBlocks.Contains(blockName)
            || block.BlockTag != BlockTags.None
            || block is BlockTrapDoor
            || block.Properties.GetString("Class") == "Ladder"
            || Array.Exists(SkippedBlockPrefixes, blockName.StartsWith)
            || (block.FilterTags != null
                && Array.IndexOf(block.FilterTags, WindowFilterTag) >= 0)
            || (block.blockMaterial != null
                && block.blockMaterial.FertileLevel >= FertileLevelThreshold);
    }

    private static bool HasTerrainBelow(World world, Vector3i pos)
    {
        for (int d = 1; d <= ScanDistance; d++)
        {
            int y = pos.y - d;
            if (y < 1)
                return false;

            BlockValue bv = world.GetBlock(pos.x, y, pos.z);

            if (bv.isair)
                continue;

            return bv.isTerrain;
        }

        return false;
    }
}

[HarmonyPatch(typeof(EntityPlayerLocal), nameof(EntityPlayerLocal.Update))]
public static class Patch_NoGap_Input
{
    private static void Postfix(EntityPlayerLocal __instance)
    {
        if (!Input.GetKeyDown(KeyCode.RightBracket)
            || __instance.HitInfo == null
            || !__instance.HitInfo.bHitValid)
            return;

        BlockValue bv = __instance.HitInfo.hit.blockValue;
        if (bv.isair || bv.Block == null)
            return;

        string blockName = bv.Block.GetBlockName();

        if (!Patch_NoGap.DisabledBlocks.Add(blockName))
        {
            Patch_NoGap.DisabledBlocks.Remove(blockName);
            GameManager.ShowTooltip(__instance, "NoGap: ENABLED for " + blockName);
        }
        else
        {
            GameManager.ShowTooltip(__instance, "NoGap: DISABLED for " + blockName);
        }
    }
}