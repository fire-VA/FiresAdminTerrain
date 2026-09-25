using FiresCore.Bridge;

namespace VerdantsAscent.Terrain.Zones
{
    /// <summary>
    /// Publishes zone mode to Core (<see cref="FiresZoneModeBridge"/>) so Core-side world content — a unique dungeon
    /// such as the mausoleum crypt — can claim a zone of its own on a one-zone-at-a-time world: it records a pending
    /// location in a closed zone beside the open land and opens that zone the location-island way (LocationReveal),
    /// then vanilla builds the location once every peer shows the new terrain. A parked zone (a converted world's
    /// explored land) is never a candidate: opening it restores what it held and vanilla builds nothing new there.
    /// Generated water reads as water until this machine knows the world's water plan, so a placer asking too early
    /// passes on the spot and asks again.
    /// </summary>
    internal static class ZoneModeCoreBridge
    {
        internal static void Register()
        {
            FiresZoneModeBridge.Register(new FiresZoneModeBridge.Hooks
            {
                IsActive = () => ZoneModeRegistry.ModeActive && ZoneModeRegistry.IsAuthority && ZoneModeRegistry.Seeded,
                IsZoneOpen = ZoneModeRegistry.IsOpen,
                HasOpenEdgeNeighbour = ZoneModeOpening.HasOpenEdgeNeighbour,
                PlanningHeight = (x, z) =>
                {
                    ZoneModeGate.EnterPlanning();
                    try { return WorldGenerator.instance.GetHeight(x, z); }
                    finally { ZoneModeGate.ExitPlanning(); }
                },
                OpenZoneForLocation = (zone, label) =>
                    ZoneModeRegistry.IsOpen(zone)
                    || ZoneModeOpening.Open(new[] { zone }, ZoneOpenReason.LocationReveal, 0L, "location: " + label).Count > 0,
                IsZoneParked = zone => ZoneModePark.TryGetParked(zone, out _),
                IsGeneratedWater = (x, z) => !Voxel.GenWaterField.EnsureWaterKnown() || Voxel.GenWaterField.IsWetAt(x, z),
            });
        }
    }
}
