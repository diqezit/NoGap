using HarmonyLib;
using Platform;
using System;
using System.Collections.Generic;

/*
 NoGap

 Vanilla terrain uses marching cubes and a per cell density sbyte value.
 Placing a normal building block next to terrain can leave that cell's density
 in a state where the terrain mesh visually pulls away from the block,
 showing a gap. A known manual fix closes this by editing only the density
 value of that one cell, without touching the block itself.

 Goal
 -----
 - Close the visual gap for blocks placed directly on or into terrain
 - Never touch blocks that are stacked in the air on top of other blocks
 - Never place, remove or replace any block - density only
 - Never apply the fix to blocks that carry a special BlockTag
   (doors, closet doors, windows, growable plants, tree trunks, gore,
   spikes) since these block types may render or behave oddly if their
   surrounding density is altered, and to trapdoors, ladders, bars,
   catwalks and railings specifically since they do not carry a
   distinguishing BlockTag

 ----------------------------------------------------------------------------
 What the mod does in order

 1) Patch ChangeBlocks
   Harmony Postfix on GameManager.ChangeBlocks, server only. This is the
   single point every block change passes through (placement, explosions,
   scripts), so it catches every real placement without needing separate
   hooks per source.

 2) Filter to real player placements
   For each entry in the batch:
   - bChangeBlockValue must be true (skip pure damage/texture updates)
   - blockValue.isair skipped (removals need no fix)
   - blockValue.ischild skipped (multiblock parts, parent already handles it)
   - changedByEntityId must resolve to an EntityPlayer (skip scripts, explosions)
   - blockValue.isTerrain must be false (only non terrain blocks get the fix)
   - Block.BlockTag must be BlockTags.None - any tagged block (Door, ClosetDoor,
     Window, GrowablePlant, TreeTrunk, Gore, Spike) is skipped entirely
     Confirmed against the shipped blocks.xml that every door variant sets
     BlockTag="Door", so the single BlockTag != None check reliably excludes
     every door in the game
   - BlockTrapDoor is skipped by C# type, as it lacks a BlockTag
   - v1.2 Ladders are skipped by checking the "Class" property for "Ladder",
     covering all vanilla and modded ladders without hardcoding names
   - v1.2 Iron and Jail Bars, Catwalks and Railings are skipped by block name
     prefix, covering all color and shape variants without hardcoding every
     single block name
   - v1.2 Windows are skipped by checking the FilterTags array for "SC_windows"
   - v1.1 Farm plot and fertile soil blocks are skipped by
     blockMaterial.FertileLevel to prevent terrain texture artifacts on
     their sides

 3) Confirm the block actually sits on terrain
   Scans straight down from the placed position one cell at a time up to
   ScanDown cells. The first non air cell found decides the outcome:
   - if it's terrain, the fix applies
   - if it's a normal block, scanning stops and the fix is skipped
   This stops the mod from touching blocks stacked in the air on top of other
   blocks, only blocks resting on or embedded in the terrain qualify.

 4) Apply the density fix
   Reads the cell's current density through WorldBase.GetDensity. If it is
   already ConnectDensity nothing is sent. Otherwise a density only
   BlockChangeInfo is built for that same cell and queued.
   ConnectDensity is -120, matching the value the manual "/" gap fix uses.
   It sits between DensityAir and DensityTerrain, so it seals the mesh without
   growing visible terrain mass. bForceDensityChange is set to true because
   ChangeBlocks otherwise clamps density on non terrain blocks and the change
   would be silently dropped.

 5) Send as one batch
   All queued density changes are sent together through GameManager.SetBlocksRPC,
   attributed to the same persistentPlayerId that triggered the original change.

 ----------------------------------------------------------------------------
 Why a ThreadStatic guard is required
   SetBlocksRPC calls ChangeBlocks internally to apply the change on the server,
   which fires this same Postfix again. Without a guard the mod would try to
   fix its own density write, loop back into SetBlocksRPC, and repeat.
   The guard is set before SetBlocksRPC is called and cleared right after,
   so the re entrant call sees it set and returns immediately.

 ----------------------------------------------------------------------------
 Important
 - Runs only when SingletonMonoBehaviour<ConnectionManager>.Instance.IsServer
   is true. Clients never evaluate this logic, changes always arrive via RPC.
 - A HashSet<Vector3i> dedupes positions within one ChangeBlocks call, since
   a single batch can reference the same cell more than once.
 - Tag based exclusion uses Block.BlockTag (single enum value per block, not
   a flag set) read from ILSpy against the shipped Assembly-CSharp. Any
   block with a tag other than BlockTags.None is excluded from the fix.
 - Cross checked against blocks.xml: every door family sets BlockTag="Door",
   confirming the BlockTag != None check alone is sufficient to exclude all
   doors. No per-class or per-name matching is needed.
 - BlockTrapDoor, Ladders, Bars, Catwalks, Railings, and Windows lack a
   unifying BlockTag, so they are excluded by C# type, Properties "Class",
   block name prefix, and FilterTags array respectively.
 - Farm plot / fertile soil blocks are excluded via MaterialBlock.FertileLevel
   to prevent terrain texture artifacts on their sides.

 ----------------------------------------------------------------------------
 Integration points (for future migration)
 GameManager.ChangeBlocks
 GameManager.SetBlocksRPC
 WorldBase.GetDensity(Vector3i)
 World.GetBlock(int,int,int)
 World.GetEntity(int)
 BlockChangeInfo(BlockValueRef, sbyte, bool)
 BlockValueRef.TryGetBlockPos
 BlockValue.isTerrain
 BlockValue.Block
 Block.BlockTag / BlockTags enum
 Block.Properties / DynamicProperties.GetString
 Block.GetBlockName()
 Block.FilterTags
 Block.blockMaterial / MaterialBlock.FertileLevel
 BlockTrapDoor
 ConnectionManager.IsServer
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

    private const int ScanDown = 6;
    private const sbyte ConnectDensity = -120;
    private const int FertileLevelThreshold = 16;
    private const string WindowFilterTag = "SC_windows";

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

        if (_blocksToChange == null)
            return;

        if (!SingletonMonoBehaviour<ConnectionManager>.Instance.IsServer)
            return;

        World world = __instance.World;
        if (world == null)
            return;

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

                BlockChangeInfo dens = new BlockChangeInfo(new BlockValueRef(pos), ConnectDensity, true)
                {
                    changedByEntityId = entityId
                };
                densityChanges.Add(dens);
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

        if (ch == null)
            return false;

        if (!ch.bChangeBlockValue)
            return false;

        if (ch.blockValue.isair)
            return false;

        if (ch.blockValue.ischild)
            return false;

        entityId = ch.changedByEntityId;
        if (entityId <= 0)
            return false;

        if (!ch.blockValueRef.TryGetBlockPos(out pos))
            return false;

        if (!(world.GetEntity(entityId) is EntityPlayer))
            return false;

        if (ch.blockValue.isTerrain)
            return false;

        Block placedBlock = ch.blockValue.Block;
        if (placedBlock != null && ShouldSkipBlock(placedBlock))
            return false;

        return true;
    }

    private static bool ShouldSkipBlock(Block block)
    {
        if (block.BlockTag != BlockTags.None)
            return true;

        if (block is BlockTrapDoor)
            return true;

        // v1.2
        if (block.Properties.GetString("Class") == "Ladder")
            return true;

        // v1.2
        string blockName = block.GetBlockName();
        if (Array.Exists(SkippedBlockPrefixes, p => blockName.StartsWith(p)))
            return true;

        // v1.2
        string[] filterTags = block.FilterTags;
        if (filterTags != null && Array.IndexOf(filterTags, WindowFilterTag) >= 0)
            return true;

        // v1.1 - Skip farm plot / fertile soil blocks (prevents terrain texture artifacts on their sides)
        MaterialBlock mat = block.blockMaterial;
        if (mat != null && mat.FertileLevel >= FertileLevelThreshold)
            return true;

        return false;
    }

    private static bool HasTerrainBelow(World world, Vector3i pos)
    {
        for (int d = 1; d <= ScanDown; d++)
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