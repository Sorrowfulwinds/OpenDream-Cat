using System;
using JetBrains.Annotations;
using Robust.Shared.GameObjects;
using Robust.Shared.Maths;
using Robust.Shared.Serialization;

namespace OpenDreamShared.Dream;

/// <summary>
/// Used by the client to refer to something that could be either its own client, a turf, or an entity
/// </summary>
/// <remarks>This should only be used on the client or when communicating with the client</remarks>
[Serializable, NetSerializable]
public struct ClientObjectReference : IEquatable<ClientObjectReference> {
    public enum RefType {
        Client,
        Turf,
        Entity
    }

    public static readonly ClientObjectReference Client = new() { Type = RefType.Client };

    public RefType Type;
    public NetEntity Entity;
    public int TurfX, TurfY, TurfZ;

    public ClientObjectReference(Vector2i turfPos, int z) {
        Type = RefType.Turf;
        (TurfX, TurfY) = turfPos;
        TurfZ = z;
    }

    public ClientObjectReference(NetEntity entity) {
        Type = RefType.Entity;
        Entity = entity;
    }

    [Pure]
    public bool Equals(ClientObjectReference other) {
        if (Type != other.Type)
            return false;

        return Type switch {
            RefType.Client => true,
            RefType.Entity => Entity == other.Entity,
            RefType.Turf => TurfX == other.TurfX && TurfY == other.TurfY && TurfZ == other.TurfZ,
            _ => false
        };
    }

    [Pure]
    public bool Equals(ClientObjectReference? other) {
        return other != null && Equals(other.Value);
    }

    public override bool Equals(object? obj) {
        return obj is ClientObjectReference other && Equals(other);
    }

    public override string ToString() {
        return Type switch {
            RefType.Client => "client",
            RefType.Turf => $"turf{{{TurfX},{TurfY},{TurfZ}}}",
            RefType.Entity => $"entity{{{Entity}}}",
            _ => "unknown ClientObjectReference"
        };
    }

    public override int GetHashCode() {
        switch (Type) {
            case RefType.Turf:
                return HashCode.Combine(Type, TurfX, TurfY, TurfZ);
            case RefType.Entity:
                return HashCode.Combine(Type, Entity);
            case RefType.Client:
            default:
                return 0;
        }
    }
}
