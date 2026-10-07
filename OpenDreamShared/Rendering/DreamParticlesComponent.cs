using System;
using System.Numerics;
using OpenDreamShared.Dream;
using Robust.Shared.Analyzers;
using Robust.Shared.GameObjects;
using Robust.Shared.GameStates;
using Robust.Shared.Maths;
using Robust.Shared.Serialization;
using Robust.Shared.ViewVariables;

namespace OpenDreamShared.Rendering;

[RegisterComponent]
[NetworkedComponent]
[AutoGenerateComponentState(true)]
public sealed partial class DreamParticlesComponent : Component {
    [ViewVariables(VVAccess.ReadOnly)] [AutoNetworkedField]
    public ParticleData Data;

    [Serializable]
    [NetSerializable]
    public sealed class ParticleData {
        [ViewVariables(VVAccess.ReadWrite)] public Vector3 Bound1;
        [ViewVariables(VVAccess.ReadWrite)] public Vector3 Bound2;
        [ViewVariables(VVAccess.ReadWrite)] public int Count;
        [ViewVariables(VVAccess.ReadWrite)] public IGeneratorVector? Drift;
        [ViewVariables(VVAccess.ReadWrite)] public IGeneratorNum? FadeIn;
        [ViewVariables(VVAccess.ReadWrite)] public IGeneratorNum? FadeOut;

        //Acceleration applied to the particles per second
        [ViewVariables(VVAccess.ReadWrite)] public IGeneratorVector? Friction;
        [ViewVariables(VVAccess.ReadWrite)] public Color[] Gradient = [];
        [ViewVariables(VVAccess.ReadWrite)] public Vector3 Gravity;

        //Increase in scale per second
        [ViewVariables(VVAccess.ReadWrite)] public IGeneratorVector? Growth;
        [ViewVariables(VVAccess.ReadWrite)] public int Height;
        [ViewVariables(VVAccess.ReadWrite)] public IGeneratorNum? Lifespan;

        //Rotation applied to the particles in degrees
        [ViewVariables(VVAccess.ReadWrite)] public IGeneratorNum? Rotation;

        //Scaling applied to the particles in (x,y)
        [ViewVariables(VVAccess.ReadWrite)] public IGeneratorVector Scale = new GeneratorNum(1);

        [ViewVariables(VVAccess.ReadWrite)] public IGeneratorVector? SpawnPosition;

        //Starting velocity of the particles
        [ViewVariables(VVAccess.ReadWrite)] public IGeneratorVector? SpawnVelocity;
        [ViewVariables(VVAccess.ReadWrite)] public float Spawning;

        //Change in rotation per second
        [ViewVariables(VVAccess.ReadWrite)] public IGeneratorNum? Spin;
        [ViewVariables(VVAccess.ReadWrite)] public ImmutableAppearance[] TextureList = [];
        [ViewVariables(VVAccess.ReadWrite)] public Matrix3x2 Transform;
        [ViewVariables(VVAccess.ReadWrite)] public int Width;
    }
}
