using OpenDreamShared.Dream;
using OpenDreamShared.Rendering;

namespace OpenDreamRuntime.Rendering;

[RegisterComponent]
public sealed partial class DMISpriteComponent : SharedDMISpriteComponent {
    [Access(typeof(DMISpriteSystem))] [ViewVariables]
    public ImmutableAppearance? Appearance;

    [ViewVariables] [Access(typeof(DMISpriteSystem))]
    public ScreenLocation ScreenLocation;
}
