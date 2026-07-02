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
 - Close the visual gap for blocks placed directly on or into terrain
 - Never touch blocks that are stacked in the air on top of other blocks
 - Never place, remove or replace any block - density only

 ----------------------------------------------------------------------------
 What the mod does in order

 1) Patch ChangeBlocks

 Harmony Postfix on GameManager.ChangeBlocks, server only. This is the single
 point every block change passes through (placement, explosions, scripts),
 so it catches every real placement without needing separate hooks per source.

 2) Filter to real player placements

 For each entry in the batch:
 - bChangeBlockValue must be true (skip pure damage/texture updates)
 - blockValue.isair skipped (removals need no fix)
 - blockValue.ischild skipped (multiblock parts, parent already handles it)
 - changedByEntityId must resolve to an EntityPlayer (skip scripts, explosions)
 - Block.shape.IsTerrain() must be false (only non terrain blocks get the fix)

 3) Confirm the block actually sits on terrain

 Scans straight down from the placed position, one cell at a time, up to
 ScanDown cells. The first non air cell found decides the outcome:
 - if it's terrain, the fix applies
 - if it's a normal block, scanning stops and the fix is skipped

 This is what stops the mod from reaching upward into stacked builds - only
 blocks resting on or embedded in the actual terrain surface qualify.

 4) Apply the density fix

 Reads the cell's current density through WorldBase.GetDensity. If it's
 already ConnectDensity nothing is sent. Otherwise a density only
 BlockChangeInfo is built for that same cell and queued.

 ConnectDensity is -120, matching the value the manual "/" gap fix mod uses.
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

 ----------------------------------------------------------------------------
 Integration points (for future migration)
 GameManager.ChangeBlocks
 GameManager.SetBlocksRPC
 WorldBase.GetDensity(Vector3i) / GetDensity(int,int,int)
 World.GetBlock(int,int,int)
 World.GetEntity(int)
 BlockChangeInfo(BlockValueRef, sbyte, bool)
 BlockValueRef.TryGetBlockPos
 Block.shape.IsTerrain()
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

    private static void Postfix(GameManager __instance, 
        PlatformUserIdentifierAbs persistentPlayerId, List<BlockChangeInfo> _blocksToChange)
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
                Vector3i pos;
                int entityId;

                if (!TryGetPlacedBlock(world, _blocksToChange[i], out pos, out entityId))
                    continue;

                if (!seen.Add(pos))
                    continue;

                if (!HasTerrainBelow(world, pos))
                    continue;

                if (world.GetDensity(pos) == ConnectDensity)
                    continue;

                BlockChangeInfo dens = new BlockChangeInfo(new BlockValueRef(pos), ConnectDensity, true);
                dens.changedByEntityId = entityId;
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

    private static bool TryGetPlacedBlock(World world, BlockChangeInfo ch, out Vector3i pos, out int entityId)
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

        Block block = ch.blockValue.Block;
        if (block == null)
            return false;

        if (block.shape.IsTerrain())
            return false;

        return true;
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

            Block b = bv.Block;
            if (b == null)
                return false;

            return b.shape.IsTerrain();
        }

        return false;
    }
}