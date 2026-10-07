using System;
using System.Numerics;
using Robust.Shared.Analyzers;
using Robust.Shared.GameObjects;
using Robust.Shared.Serialization;

namespace OpenDreamShared.Rendering;

[Virtual]
public class SharedClientImagesSystem : EntitySystem {
    [Serializable]
    [NetSerializable]
    public sealed class AddClientImageEvent(NetEntity attachedEntity, Vector3 turfCoords, NetEntity imageEntity)
        : EntityEventArgs {
        public NetEntity
            AttachedEntity = attachedEntity; //if this is NetEntity.Invalid (ie, a turf) use the TurfCoords instead

        public NetEntity ImageEntity = imageEntity;
        public Vector3 TurfCoords = turfCoords;
    }

    [Serializable]
    [NetSerializable]
    public sealed class RemoveClientImageEvent(NetEntity attachedEntity, Vector3 turfCoords, NetEntity imageEntity)
        : EntityEventArgs {
        public NetEntity
            AttachedEntity = attachedEntity; //if this is NetEntity.Invalid (ie, a turf) use the TurfCoords instead

        public NetEntity ImageEntity = imageEntity;
        public Vector3 TurfCoords = turfCoords;
    }
}
