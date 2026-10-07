using System;
using OpenDreamShared.Dream;
using Robust.Shared.GameObjects;
using Robust.Shared.GameStates;
using Robust.Shared.Serialization;

namespace OpenDreamShared.Rendering;

[NetworkedComponent]
public abstract partial class SharedDMISpriteComponent : Component {
    [Serializable]
    [NetSerializable]
    public sealed class DMISpriteComponentState : ComponentState {
        public readonly uint? AppearanceId;
        public readonly ScreenLocation ScreenLocation;

        public DMISpriteComponentState(uint? appearanceId, ScreenLocation screenLocation) {
            AppearanceId = appearanceId;
            ScreenLocation = screenLocation;
        }
    }
}
