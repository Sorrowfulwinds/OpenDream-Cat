using Robust.Shared.Serialization;
using System;
using Robust.Shared.Audio.Components;

namespace OpenDreamShared.Dream;

[Serializable, NetSerializable, Flags]
public enum AtomDirection : byte {
    None = 0,

    North = 1,
    South = 2,
    East = 4,
    West = 8,

    Up = 16,
    Down = 32,

    Northeast = North | East,
    Southeast = South | East,
    Southwest = South | West,
    Northwest = North | West
}

public static class AtomDirectionExtensions {
    /// <summary>
    /// Converts integers to a valid single dir.
    /// </summary>
    /// <param name="value"></param>
    /// <returns>Single AtomDirection or AtomDirection.None if invalid.</returns>
    public static AtomDirection ToIconAtomDirection(this int value) {
        switch (value) {
            case 0: return AtomDirection.South;
            case 1: return AtomDirection.North;
            case 2: return AtomDirection.South;
            case 4: return AtomDirection.East;
            case 5: return AtomDirection.Northeast;
            case 6: return AtomDirection.Southeast;
            case 8: return AtomDirection.West;
            case 9: return AtomDirection.Northwest;
            case 10: return AtomDirection.Southwest;
            default: return AtomDirection.None;
        }
    }
}
