using System;
using System.Collections.Generic;

namespace VerdantsAscent.Terrain.Zones
{
    // Where a zone's parked bytes sit. Parked and Restored are the zone's own copy, before and after it returned (one or
    // the other); Overridden is what a wrong seed or the Chaos Hammer set aside for undo; Unreadable keeps bytes that
    // failed to load, for inspection. Each is the zone file extension of the same name in the per-zone layout.
    internal enum ParkSlot : byte
    {
        Parked = 1,
        Restored = 2,
        Overridden = 3,
        Unreadable = 4,
    }

    // One record read in a pass over many: its bytes, or why it could not be read.
    internal struct ParkRead
    {
        internal Vector2s Zone;
        internal byte[] Payload;
        internal string Problem;
    }

    // A world's parked zones on disk: the pack (ZoneModeParkPack, D6c), or one file per zone (ZoneModeParkZoneFiles) until
    // a world's first load on a pack build repacks them. The payload is always a zone file's exact bytes
    // (ZoneModePark.WriteZone); a store only keeps, moves and returns it. Main thread only.
    internal interface IParkStore
    {
        string Folder { get; }

        // For logs: what holds the park and how big it is.
        string Describe();

        // Every Parked zone with its record, as the index knows it.
        IEnumerable<KeyValuePair<Vector2s, ZoneModePark.ParkedZone>> Parked();

        List<Vector2s> ZonesIn(ParkSlot slot);

        bool Has(Vector2s zone, ParkSlot slot);

        bool TryRead(Vector2s zone, ParkSlot slot, out byte[] payload, out string problem);

        // Every zone's bytes in the slot, in the order the store reads fastest, one open file for the whole pass.
        IEnumerable<ParkRead> ReadAll(ParkSlot slot, ICollection<Vector2s> zones);

        // The record kept with the bytes (from its header), for a slot the index does not list details for.
        bool TryGetDetails(Vector2s zone, ParkSlot slot, out ZoneModePark.ParkedZone parked);

        // Moves a zone's bytes to another slot, replacing what that slot held. False (logged) when it could not.
        bool Move(Vector2s zone, ParkSlot from, ParkSlot to);

        void Remove(Vector2s zone, ParkSlot slot);

        // New bytes for a zone's slot, replacing what it held.
        void Write(Vector2s zone, ParkSlot slot, ZoneModePark.ParkedZone parked, byte[] payload);

        // Makes every change since the last commit survive a restart. `parked` is the park's index of Parked zones.
        void Commit(IDictionary<Vector2s, ZoneModePark.ParkedZone> parked);
    }
}
