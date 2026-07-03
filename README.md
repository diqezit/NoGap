# NoGap

Harmony mod for 7 Days to Die that fixes the terrain gap under placed blocks

Tested on 3.0

## Problem

7DTD terrain uses marching cubes with a density value per cell When you place a block on terrain the density of the cell under it doesn't always update to match so you get a visible gap between the block and the ground There's an old manual fix for this where you edit the density of just that one cell with the console or editor This mod does that automatically

## What it does

- Hooks GameManager.ChangeBlocks
- Checks if a placed block is a real player placement not scripts or explosions
- Checks if the block is actually sitting on terrain not on other blocks
- If yes fixes the density of the terrain cell below it
- Sends it as a normal block change so it looks like nothing special happened

Doesn't touch

- blocks stacked on blocks
- doors windows plants trees gore spikes tagged blocks
- trapdoors no tag in this version excluded manually
- terrain blocks
- block data itself ever density only

## Server side only

Runs only if ConnectionManager.IsServer is true Clients get the result over RPC same as any other block change don't need to install it but doesn't hurt to

Has a reentrancy guard because applying the fix calls SetBlocksRPC which calls ChangeBlocks again which would otherwise trigger the patch on itself

## Install

Drop the folder in Mods That's it

## Compatibility

Should be fine with other block or building mods since it only fires after ChangeBlocks and only ever writes density never blocks If you have a mod adding new terrain adjacent building mechanics that also messes with density test together

Built against 3.0 internals BlockValue.isTerrain Block.BlockTag WorldBase.GetDensity etc Might break on other versions if these change
