using System.Collections.Generic;
using FiresCore.Sync;
using UnityEngine;

namespace VerdantsAscent.Terrain.Zones
{
    // A stable public surface on the Chunk Generator for tools and test bots in other mods (FDT's multiplayer test bot;
    // Tools\CHUNK_GENERATOR.md "Two-player test"). Every action runs the same client code the windows' buttons run: the
    // same inventory removal and the same RPCs to the machine's ZDO owner. So a bot exercises what a human does, minus
    // the clicks. Reach it by name without a reference: Type.GetType("VerdantsAscent.Terrain.Zones.ChunkGeneratorApi,
    // FiresAdminTerrain").
    public static class ChunkGeneratorApi
    {
        public const string MachinePrefab = "FiresChunkMachine_Meadows";

        // What the machine's ZDO holds right now, as every client sees it.
        public sealed class State
        {
            public string Recipe;
            public int Loaded;
            public int Burned;
            public int Capacity;
            public int BatchFuel;
            public long Owner;
            public bool BonusArmed;
            public Dictionary<string, int> Fuel = new Dictionary<string, int>();
            public Dictionary<string, int> Counts = new Dictionary<string, int>();

            // What the current batch still needs delivered (any mix of Burns(Recipe)), and what one batch makes.
            public int Remaining;
            public string OutputPrefab;
            public int OutputCount;

            // Where a finished batch's seeds appear (the machine's output point, world position).
            public Vector3 OutputPoint;
        }

        // A closed zone a seed can open now, from Frontier. X and Y are ZONE coordinates (the zone's x and z on the map), not
        // height.
        public sealed class FrontierZone
        {
            public int X;
            public int Y;
            public Vector3 Center;
            public float Distance;
            public string PlannedBiome;

            // The zone's nearest point to `from` (y = from's height): standing within 48 m of it is in reach for PlantSeed.
            public Vector3 EdgePoint;
        }

        // The server's answer to a PlantSeed (or a hammer strike) on this client: zone x, zone y (map x/z), opened, message.
        // Raised on the main thread.
        public static event System.Action<int, int, bool, string> SeedResult;

        internal static void RaiseSeedResult(Vector2s zone, bool opened, string message) => SeedResult?.Invoke(zone.x, zone.y, opened, message);

        // Closed zones a seed can open now (inside the world, beside open land), nearest first; at most `max` (pass 8).
        public static List<FrontierZone> Frontier(Vector3 from, int max = 8)
        {
            var zones = new List<FrontierZone>();
            foreach (var entry in ZoneModeChunkSpawner.Frontier(from, max))
            {
                Vector3 center = ZoneSystem.GetZonePos(entry.Zone);
                zones.Add(new FrontierZone
                {
                    X = entry.Zone.x,
                    Y = entry.Zone.y,
                    Center = center,
                    Distance = entry.Distance,
                    PlannedBiome = WorldGenerator.instance != null ? WorldGenerator.instance.GetBiome(center).ToString() : "",
                    EdgePoint = new Vector3(
                        Mathf.Clamp(from.x, center.x - ZoneOpenSet.ZoneSize * 0.5f, center.x + ZoneOpenSet.ZoneSize * 0.5f),
                        from.y,
                        Mathf.Clamp(from.z, center.z - ZoneOpenSet.ZoneSize * 0.5f, center.z + ZoneOpenSet.ZoneSize * 0.5f)),
                });
            }
            return zones;
        }

        // The hammer's "Yes" for a bot: plants one seed of `seedPrefab` from the player's inventory in zone (zoneX, zoneY) (map
        // x/z). The player must be within reach of that zone (48 m of its edge). Null when the request went out (the answer comes
        // on SeedResult, and the seed is used only if the zone opened), else why not.
        public static string PlantSeed(Player player, string seedPrefab, int zoneX, int zoneY)
            => ZoneModeChunkSpawner.PlantForBot(player, seedPrefab, new Vector2s(zoneX, zoneY));

        // The generator nearest `position` within `range` metres, or null.
        public static Smelter Nearest(Vector3 position, float range)
        {
            Smelter nearest = null;
            float best = range * range;
            foreach (var smelter in Object.FindObjectsByType<Smelter>(FindObjectsSortMode.None))
            {
                if (!ZoneModeChunkGenerator.IsOurs(smelter.GetComponent<ZNetView>())) continue;
                float distance = (smelter.transform.position - position).sqrMagnitude;
                if (distance > best) continue;
                best = distance;
                nearest = smelter;
            }
            return nearest;
        }

        public static State Read(Smelter machine)
        {
            var nview = machine != null ? machine.GetComponent<ZNetView>() : null;
            if (!ZoneModeChunkGenerator.IsOurs(nview)) return null;
            var zdo = nview.GetZDO();
            var state = new State
            {
                Recipe = ZoneModeChunkGenerator.CurrentRecipe(zdo).Id,
                Loaded = ZoneModeChunkGenerator.Loaded(zdo),
                Burned = ZoneModeChunkGenerator.Burned(zdo),
                Capacity = ZoneModeChunkGenerator.Capacity,
                BatchFuel = ZoneModeChunkTiers.BatchFuel,
                Owner = zdo.GetOwner(),
                BonusArmed = ZoneModeChunkGenerator.BonusArmed(zdo),
            };
            foreach (var entry in ZoneModeChunkGenerator.FuelEntries(zdo)) state.Fuel[entry.Key] = entry.Value;
            foreach (var entry in ZoneModeChunkGenerator.CountEntries(zdo)) state.Counts[entry.Key] = entry.Value;
            var recipe = ZoneModeChunkGenerator.CurrentRecipe(zdo);
            state.Remaining = Mathf.Max(0, state.BatchFuel - state.Burned - state.Loaded);
            state.OutputPrefab = recipe.SpawnerPrefab;
            state.OutputCount = recipe.SeedsPerBatch;
            state.OutputPoint = machine.m_outputPoint != null ? machine.m_outputPoint.position : machine.transform.position;
            return state;
        }

        // Recipe ids this player has learned, in the order the hopper lists them.
        public static List<string> KnownRecipes()
        {
            var known = new List<string>();
            foreach (var tier in ZoneModeChunkTiers.All)
                if (ZoneModeChunkGenerator.IsKnown(tier)) known.Add(tier.Id);
            return known;
        }

        // The fuel item prefabs a recipe burns that this game has: what the fuel window lists before it filters to what
        // the player carries. Empty for an unknown recipe id.
        public static List<string> Burns(string recipeId)
        {
            var burns = new List<string>();
            foreach (var tier in ZoneModeChunkTiers.All)
            {
                if (tier.Id != recipeId) continue;
                foreach (string item in ZoneModeChunkTiers.Burns(tier))
                    if (ZoneModeChunkGenerator.TryGetFuel(item, out _)) burns.Add(item);
                break;
            }
            return burns;
        }

        // The fuel window's OK: takes up to `amount` of the item from the player's inventory (never more than it carries
        // or than fits) and sends it to the owner, which drops anything it cannot take beside the machine. Returns how
        // many left the inventory.
        public static int AddFuel(Smelter machine, Player player, string itemPrefab, int amount)
            => ZoneModeChunkGenerator.TryGetFuel(itemPrefab, out var fuel) ? ZoneModeChunkGenerator.AddFuel(machine, player, fuel, amount) : 0;

        // The recipe window's "Use this recipe": only a learned recipe, as the window lists only those.
        public static bool ChooseRecipe(Smelter machine, string recipeId)
        {
            foreach (var tier in ZoneModeChunkTiers.All)
            {
                if (tier.Id != recipeId) continue;
                if (!ZoneModeChunkGenerator.IsKnown(tier)) return false;
                ZoneModeChunkGenerator.ChooseRecipe(machine, tier);
                return true;
            }
            return false;
        }

        // The fires_chunkgen_bonus command's request; the owner refuses a non-admin sender and logs it. Returns false when
        // the local player is not an admin, which is what the command answers too.
        public static bool ArmBonus(Smelter machine)
            => AdminSyncing.IsLocalAdmin() && ZoneModeChunkGenerator.RequestBonus(machine);
    }
}
