# Fires Admin Terrain

**Updated for Valheim 1.0.**

The world-shaping toolkit of the Fires mod family. Sculpt terrain, dig real 3D tunnels and caves, place water,
lava, tar, ice and snow at any height, paint biomes, and carve rivers and lakes into an existing world. Or let
it generate whole new FIRES worlds, including "one zone at a time" worlds where you open the map square by
square.

Every edit is saved with the world, so every player sees the same ground.

## Requirements

- **FiresUnifiedCore** 0.2.297 or newer.
- **Install on the server and every client, on the same version.** Nothing stops a mismatch, but a player
  without the mod (or on another version) sees different ground from everyone else. In a one-zone world, a client
  on an older version logs `the server speaks zone protocol N`, and its closed zones won't match the server's.
- Optional:
  - **Infinity Hammer**: its ruler aims the terrain brushes, the mouse wheel sets their size, and holding the
    mouse paints continuously.
  - **ServerDevcommands**: lets admins run the editing commands on a dedicated server, and its `undo` / `redo`
    also undo terrain brushes.
  - **FiresTossinShade**: the water shaders. Without it, water uses a plainer vanilla look.
- **Incompatible: Valheim Performance Optimizations** (`dev.ontrigger.vpo`). Fires Admin Terrain cannot be used with it;
  BepInEx won't load Fires Admin Terrain while VPO is installed.

## Upgrading from 0.2.382 or earlier

- `[VoxelTerrain World] Terrain Mode` now defaults to `Off`, but an existing config keeps its old value, which
  was `FullVoxel`. Check it before you start the server.
- With any mode other than `Off`, rivers and lakes get carved into vanilla-generated worlds, and trees and
  bushes standing in the new water are removed. Set `Hydrology Enabled` and `Hydrology Place Water` to false if
  you only want tunnels.
- Voxel edits saved by 0.2.384 or older are removed on load.
- FIRES-generated worlds generate differently now, so back them up first. A baked cache from before 0.2.423 has
  to be baked again.
- Expect a one-time rebuild of the terrain cache and a redrawn minimap on the first join.

## Defaults that differ from vanilla

- `Vegetation density scale` 0.7 and `Clutter density scale` 0.55 (`[VoxelTerrain Performance]`): fewer trees
  and bushes in newly generated zones, and less grass. Set both to 1 for vanilla density.
- The New World screen has extra rows under Seed for the world's terrain and features (see Creating and cloning
  worlds), and every world made there keeps those choices.
- Pickaxe hits on voxel ground dig 3D tunnels. Wards and no-build areas still apply.

## Terrain Mode

Worlds created or cloned from the main menu keep their own terrain choices, and the server and every client follow
them whatever their Terrain Mode and feature settings say; a machine with the voxel module switched off still runs
without it. `[VoxelTerrain World] Terrain Mode` sets the defaults the New World screen starts with,
and decides what the mod does to a world without saved choices. The server's value applies to everyone.

| Mode | What happens |
|---|---|
| `Off` (default) | Vanilla terrain. Brushes, water, ice and snow work; voxel tools only add shapes. |
| `ZoneReplace` | Vanilla ground until you edit it. A zone becomes voxel terrain on its first voxel brush or pickaxe dig. On vanilla-generated worlds, rivers and lakes are carved in as well. |
| `PlacedOnly` | Currently the same as `ZoneReplace`. |
| `FullVoxel` | Voxel terrain everywhere near players, with generated caves, caverns and sinkholes: needed by both voxel wrap and full voxel worlds, and by FIRES worlds, sky islands and skylands. A world made on the New World screen or by cloning keeps its own terrain choice and switches this on for itself; this setting only rules worlds without saved choices and sets the screen's defaults. |

Full voxel (a world's terrain choice) is not the same as `FullVoxel` (this config mode): a voxel wrap world runs in `FullVoxel` mode too.

## Tools

- **V-tools**, on the Chaos Hammer's Voxel tab for admins: Vcarve, Vfill, Vdelete, Vcube, Vsphere, Vriver
  (drag a river bed) and Vpour (drag a ribbon of water). The mouse wheel sets the size and PgUp/PgDn the height.
  - **Vlevel** flattens the ground to the height you first click, cutting what stands above and filling what lies below.
  - **Vpool** digs and fills a pool, or uses the water you click. Click a second spot or water and it carves and fills a
    river between the two along the low ground (click the higher one first). LeftAlt + click fills a dip to its rim
    without digging.
  - **Vfall** hangs a waterfall from the edge you click (a cliff or a sky island's rim), with mist and a pool below.
  - **Vmop** removes water for good, generated or drawn; LeftAlt brings back what it removed. Redraw with the tools above.
  - Every tool works on sky islands too. Each has a draw speed under `VoxelTerrain Tools`; hide them with
    `Show tools in build menu`.
- **Console commands**: every brush, water, ice, snow, paint and world tool is a command (`va_*`, `place_*`,
  `ice_*`, `paint_*`, `fires_*`). Most need devcommands. In game, the full list with arguments is in the pause
  menu under **? Help > Fires Admin Terrain**.

## What's in it

- **Terrain sculpting**: flat, raise, lower, smooth, grab, noise and floating-island brushes on persistent 64 m
  tiles, with ground paint, per-tile biomes, hole punching and undo.
- **Voxel terrain**: true 3D edits for overhangs, tunnels and caves. In `FullVoxel`, the world grows its own cave
  networks with a dark cave mood. The server dresses them with the biome's pickables, ore, rubble, props and
  hanging roots. Chests and picked stones, bones and crystals restock, and monster spawners re-arm, a few in-game
  days later while nobody is near (`[VoxelTerrain Cave Content]`). Each client adds loose floor clutter in scattered
  patches (`[VoxelTerrain Cave Clutter]`).
- **Water and liquids**: water, lava, tar, boiling and freezing water, waterfalls, springs that flow downhill,
  and pools that settle to their level, at any height and each with its own waterline. You can swim in them and
  sail boats on river currents. Where water steps down (a pond spilling into a lake, a channel dropping to a lower
  reach, a sheet ending over an edge) it pours over as a waterfall that follows the ground below.
- **Ice and snow**: floating ice with holes you can climb out of, ice sheets on land, and snow layers, all
  shapeable.
- **Biome paint**: paint snow, lava, swamp, forest, plains, mistlands or meadows onto vanilla ground, or set a
  biome per sculpted tile.
- **Rivers and lakes for existing worlds**: winding river channels and dug-out lakes with beaches, reeds and
  lilies.
- **FIRES worlds**: choose the Fires terrain generator on the New World screen (it needs voxel terrain) for FIRES
  terrain: continents, erosion, climate-driven biomes, carved rivers, lakes, volcanoes and sky islands. You can
  also start from your own greyscale heightmap or biome image.
- **Faster joins**: clients of a dedicated server keep the world map between joins, and a server's pre-built
  terrain downloads once.

## One zone at a time (experimental)

You start on a small patch of land in an endless sea and open the world one 64 m square at a time. Choose it on
the New World screen or when cloning a world, or start a dedicated server's brand-new world with the `-fireszones`
launch flag. It runs on stock Valheim generation for now.

- **Chunk Generators make Chunk Seeds.** An indestructible Meadows Chunk Generator stands next to the start, and it
  comes back within a minute if it ever goes missing. Feed a
  generator any two different materials of its biome, 50 of each, for one seed. Generators burn no fuel, the Meadows
  one takes every biome's materials, and hovering one shows what it has counted so far.
- **The Chunk Hammer plants them.** Your first Chunk Hammer is in a chest in the start cave, inside the rocky hill
  beside the start temple; the cave mouth faces the temple. A world without that cave (a converted world) hides the
  chest in a cave under the start, or above ground among the trees and stones near the Stone Circle. The chest can't be broken; once its hammer is taken it
  disappears and comes back the next in-game day with a new one. Once you have held a hammer, you can craft more with
  the build hammer's recipe. Seeds are its ammo: with the hammer in hand, look
  out over the closed land beside your own (within 48 m). A ghost of the square shows the land that will rise there,
  and turns red when it can't. Strike, confirm, and the square rises. A seed and 1 durability are only used up when
  the land rises.
- **A seed raises its own biome.** A square the world planned as that biome rises as itself. Any other square, sea
  included, gets real land of that biome. Seeds of one biome side by side continue the same land, and different lands
  meet as straight cliffs.
- **Each boss unlocks the next biome's generator** on the hammer. A new generator also takes 100 each of the previous
  biome's pair, which is how its first seeds get made:

  | Biome | Unlocked by | Some of its materials | Or 100 each of |
  |---|---|---|---|
  | Black Forest | Eikthyr | Round Log, Copper Ore, Tin Ore, Greydwarf Eye, Troll Hide | Stone + Wood |
  | Swamp | The Elder | Iron Scrap, Ancient Bark, Guck, Bloodbag, Ooze | Round Log + Copper Ore |
  | Mountain | Bonemass | Silver Ore, Obsidian, Wolf Pelt, Freeze Gland, Crystal | Iron Scrap + Ancient Bark |
  | Plains | Moder | Black Metal Scrap, Flax, Barley, Lox Pelt, Needle | Silver Ore + Obsidian |
  | Mistlands | Yagluth | Yggdrasil Wood, Black Marble, Soft Tissue, Carapace, Sap | Black Metal Scrap + Flax |
  | Ashlands | The Queen | Ashwood, Grausten, Flametal Ore, Charred Bone, Asksvin Hide | Yggdrasil Wood + Black Marble |
  | Deep North | Fader | Timberwood, Petrified Tissue, Ice, Moose Hide, Seal Pelt | Ashwood + Grausten |

- **Location seeds** come from the forge, made from two kinds of Chunk Seed. A biome with Meadows seeds gives its
  dungeon, a biome with the next biome's seeds gives its boss altar, and the other pairs give landmarks. The place
  rises on land copied from where the world planned one.
- **Unique seeds** (15 each of two biome seeds plus 3 location seeds) raise one-of-a-kind places again: Haldor's and
  Hildir's camps, the Bog Witch, the Forge of Potential, the three Places of Mystery and the Rock Clearing.
- **The Eye of the Beholder**, a helmet from the workbench, names the land a square was meant to be while you aim the
  Chunk Hammer.
- **Hugin explains it.** Hugin comes down at your first spawn, the first time you hold a Chunk Hammer and at your first
  Chunk Generator, once per character like the vanilla tutorials.
- **Progression can't be blocked.** Boss altars, and the places their summon items and key materials come from, rise
  on their own the first time a player comes near. The server's `[Zone Mode] Progression locations` lists them.
- Some points of interest rise from the sea as islands when you come near, and closed edges are cliff walls with
  cave mouths and waterfalls. Skylands float above your land, each with a biome (mostly the one below) and its
  weather. They come in many shapes and sizes, from small islets to great skylands several zones across, and a rare
  few form chains or atolls round a lagoon. The bigger ones carry hills, cliff terraces and tiers, ponds, and rivers
  that spill off the rim as falls, plus caves dressed from their own biome and sometimes a place from their biome,
  set away from the water.
- You can't build, dig or teleport over closed sea, and nothing spawns there. Admins can open squares with
  `fires_zones_open`, and `fires_zones_find` shows where the starter generator and the hammer chest are.
- A square can never be closed again. Existing worlds become one-zone worlds by cloning them (below).
- **Converted worlds keep what you built.** When a cloned world first loads as one zone at a time, every zone it had
  already explored is parked: closed, with everything in it saved. Opening that square again brings it all back as
  it was. Only a seed planted while wearing the Eye of the Beholder, whose land doesn't match that square's own land
  (its biome, or the place it held), raises that seed's land instead and discards what was parked there. Before you
  confirm, the planting prompt says which will happen and how many built pieces stood there. A converted world gets no start-cave hill.

## Creating and cloning worlds

- **New World:** under Seed, pick the terrain generator (Stock Valheim or Fires), terrain (Heightmap, Voxel wrap or
  Full voxel), one zone at a time, sky islands, rivers and lakes, and caves. The rows start from your config's
  defaults and are always there while FAT is installed; `[Zone Mode] Offer at world creation = false` hides only the
  one-zone row. If a combination can't work, the reason shows in red and Done waits until you change it. An amber
  note says when a choice won't take effect on this machine, for example with the voxel module switched off; the
  world keeps the choice either way. Every world made here keeps these answers, so its name no longer decides how it
  is generated, and the server and every client follow them whatever their Terrain Mode and feature settings say.
- **Clone:** right-click a world in Select World and pick Clone as one zone at a time, full voxel, voxel wrap or
  heightmap. A dialog opens, set up for that choice: the new name, terrain (or as the original), one zone at a
  time, and sky islands, rivers and lakes and caves (as the original, on or off). The original world is never
  touched: the copy gets its own world ID and brings FAT's baked terrain, painted biomes, and cave and island records along.
- Three rules apply to both: the Fires generator needs voxel terrain; a stock-generated world set to full voxel runs
  as voxel wrap, its voxel ground built from its own heightmap; and one zone at a time runs on either generator.
- From the main-menu console:
  `fires_world_clone <world> <new name> [chunks] [heightmap|voxelwrap|voxelonly] [sky=on|off] [rivers=on|off] [caves=on|off]`

## Back up these files with the world

FAT keeps per-world data next to the world save, in the `FiresWorldGen` folder: every `firesgen_<world>*` file and
folder (zones, islands, skylands, caves, the hammer chest, the world's saved choices, its mode and bake, and a
converted world's parked zones and conversion marker), plus `<world>.biomedata`. Back them all up with the world,
and copy them whenever you move it. Without them a FIRES
world loads as a normal one. A one-zone world also keeps a copy of its zone state inside the save, so it stays a
one-zone world, but the rest of its data, including its parked zones, is still in that folder.

## Performance

- `Terrain Mode` `Off` keeps the voxel systems asleep.
- `[VoxelTerrain Performance]`: `Voxel wrap radius (headless)` is the biggest dedicated-server lever. On clients,
  use `Terrain casts shadows`, `Grass render distance`, the two density scales and the impostor settings.
- `Chunk cache` keeps generated chunks on disk (up to 512 MB by default) so revisits load instantly.
- FIRES worlds take a few minutes to generate the first time and are cached afterwards. A high
  `WorldGen Resolution` uses a lot of memory.

## Known issues

- One zone at a time is new:
  - skylands don't show on the minimap
  - the later tiers' costs are still being tuned
- Dedicated servers keep vanilla ground under voxel terrain unless `World Terrain Mode` is `VoxelOnly`.
  Tunnels and carves are built on each player's machine.
- The waterfall shader is Direct3D 11 only and may not draw on Vulkan.
- Small gaps can show along some riverbanks.

## Issues / questions

https://discord.gg/zQRgHbqms4
