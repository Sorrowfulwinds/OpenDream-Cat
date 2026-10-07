using OpenDreamRuntime.Resources;
using OpenDreamShared.Dream;
using OpenDreamShared.Rendering;

namespace OpenDreamRuntime.Objects.Types;

public sealed class DreamObjectParticles : DreamObject {
    private List<string> _iconStates = new();
    private List<MutableAppearance> _icons = new();
    private Dictionary<DreamObjectMovable, DreamParticlesComponent> _owners;
    private DreamParticlesComponent.ParticleData _particlesData;

    public DreamObjectParticles(DreamObjectDefinition objectDefinition) : base(objectDefinition) {
        _owners = [];
        _particlesData = new DreamParticlesComponent.ParticleData();

        //populate component with settings from type
        foreach (KeyValuePair<string, DreamValue> kv in ObjectDefinition.Variables)
            if (kv.Key is not ("parent_type" or "type" or "vars"))
                SetVar(kv.Key, kv.Value);
    }

    protected override void HandleDeletion() {
        foreach (DreamObjectMovable owner in _owners.Keys)
            if (owner.Particles != this)
                // This should never happen, but leaving things in an invalid state would be even worse
                RemoveOwner(owner);
            else
                owner.Particles = null; // calls RemoveOwner

        _icons = null!;
        _iconStates = null!;
        _particlesData = null!;
        _owners = null!;

        base.HandleDeletion();
    }

    public void AddOwner(DreamObjectMovable movable) {
        if (Deleting || Deleted)
            throw new InvalidOperationException($"{movable} tried to add new owner to deleted particles");
        if (movable.Deleting || movable.Deleted)
            throw new InvalidOperationException($"deleted movable tried to own particles {this}");
        if (_owners.ContainsKey(movable))
            throw new ArgumentException($"{movable} tried to own {this} while already owning");

        DreamParticlesComponent component = new() {Data = _particlesData};
        EntityManager.AddComponent(movable.Entity, component);
        _owners.Add(movable, component);
    }

    public void RemoveOwner(DreamObjectMovable movable) {
        if (!_owners.TryGetValue(movable, out DreamParticlesComponent? component))
            throw new ArgumentException($"{movable} tried to remove {this} but doesn't own it");

        EntityManager.RemoveComponent(movable.Entity, component);
        component.Data = null!;
        _owners.Remove(movable);
    }

    private void MarkDirty() {
        foreach ((DreamObjectMovable movable, DreamParticlesComponent component) in _owners)
            EntityManager.Dirty(movable.Entity, component);
    }

    private bool TryConvertVector(DreamValue value, out Vector3 newVec) {
        if (value.TryGetValueAsIDreamList(out IDreamList? vectorList) && vectorList.GetLength() >= 3) {
            using DreamValue boundXValue = vectorList.GetValue(new DreamValue(1));
            using DreamValue boundYValue = vectorList.GetValue(new DreamValue(2));
            using DreamValue boundZValue = vectorList.GetValue(new DreamValue(3));
            float boundX = boundXValue.UnsafeGetValueAsFloat();
            float boundY = boundYValue.UnsafeGetValueAsFloat();
            float boundZ = boundZValue.UnsafeGetValueAsFloat();

            newVec = new Vector3(boundX, boundY, boundZ);
            return true;
        }

        if (value.TryGetValueAsDreamObject<DreamObjectVector>(out DreamObjectVector? vectorVector /*lol*/)) {
            newVec = vectorVector.AsVector3;
            return true;
        }

        newVec = default;
        return false;
    }

    protected override void SetVar(string varName, DreamValue value) {
        //good news, these only update on assignment, so we don't need to track the generator, list, or matrix objects
        switch (varName) {
            case "width": //num
                _particlesData.Width = (int)value.UnsafeGetValueAsFloat();
                break;
            case "height": //num
                _particlesData.Height = (int)value.UnsafeGetValueAsFloat();
                break;
            case "count": //num
                if (!value.TryGetValueAsInteger(out int count))
                    break;

                _particlesData.Count = count;
                break;
            case "spawning": //num
                _particlesData.Spawning = value.UnsafeGetValueAsFloat();
                break;
            case "bound1": //list or vector
                if (TryConvertVector(value, out Vector3 bound1Vec))
                    _particlesData.Bound1 = bound1Vec;

                break;
            case "bound2": //list or vector
                if (TryConvertVector(value, out Vector3 bound2Vec))
                    _particlesData.Bound2 = bound2Vec;

                break;
            case "gravity": //list or vector
                if (TryConvertVector(value, out Vector3 gravityVec))
                    _particlesData.Gravity = gravityVec;

                break;
            case "gradient": //color gradient list
                if (value.TryGetValueAsDreamList(out DreamList? colorList)) {
                    var grad = new Color[colorList.GetLength()];
                    var i = 0;
                    foreach (DreamValue colorValue in colorList.EnumerateValues()) {
                        if (!colorValue.TryGetValueAsString(out string? colorStr))
                            continue;
                        if (!ColorHelpers.TryParseColor(colorStr, out Color c, string.Empty))
                            continue;

                        grad[i++] = c;
                    }

                    _particlesData.Gradient = grad;
                }

                break;
            case "transform": //matrix
                if (value.TryGetValueAsDreamObject<DreamObjectMatrix>(out DreamObjectMatrix? matrix)) {
                    float[] m = DreamObjectMatrix.MatrixToTransformFloatArray(matrix);
                    _particlesData.Transform = new Matrix3x2(m[0], m[1], m[2], m[3], m[4], m[5]);
                }

                break;
            case "icon": //list or icon
                _icons.Clear();
                if (value.TryGetValueAsIDreamList(out IDreamList? iconList)) {
                    foreach (DreamValue iconValue in iconList.EnumerateValues()) {
                        if (!DreamResourceManager.TryLoadIcon(iconValue, out IconResource? iconRsc))
                            continue;

                        MutableAppearance iconAppearance = MutableAppearance.Get();
                        iconAppearance.Icon = iconRsc.Id;
                        _icons.Add(iconAppearance);
                    }
                } else if (DreamResourceManager.TryLoadIcon(value, out IconResource? iconRsc)) {
                    MutableAppearance iconAppearance = MutableAppearance.Get();
                    iconAppearance.Icon = iconRsc.Id;
                    _icons.Add(iconAppearance);
                }

                List<ImmutableAppearance> immutableAppearances = new();
                foreach (MutableAppearance icon in _icons)
                foreach (string iconState in _iconStates) {
                    MutableAppearance iconCombo = MutableAppearance.GetCopy(icon);
                    iconCombo.IconState = iconState;
                    immutableAppearances.Add(AppearanceSystem!.AddAppearance(iconCombo));
                }

                _particlesData.TextureList = immutableAppearances.ToArray();
                break;
            case "icon_state": //list or string
                _iconStates.Clear();
                if (value.TryGetValueAsIDreamList(out IDreamList? iconStateList))
                    foreach (DreamValue iconValue in iconStateList.EnumerateValues()) {
                        if (!iconValue.TryGetValueAsString(out string? iconState))
                            continue;

                        _iconStates.Add(iconState);
                    }
                else if (value.TryGetValueAsString(out string? iconState)) _iconStates.Add(iconState);

                immutableAppearances = new List<ImmutableAppearance>();
                foreach (MutableAppearance icon in _icons)
                foreach (string iconState in _iconStates) {
                    MutableAppearance iconCombo = MutableAppearance.GetCopy(icon);
                    iconCombo.IconState = iconState;
                    immutableAppearances.Add(AppearanceSystem!.AddAppearance(iconCombo));
                }

                _particlesData.TextureList = immutableAppearances.ToArray();
                break;
            case "lifespan": //num or generator
                if (value.TryGetValueAsFloat(out float floatValue))
                    _particlesData.Lifespan = new GeneratorNum(floatValue);
                else if (value.TryGetValueAsDreamObject<DreamObjectGenerator>(out DreamObjectGenerator? generator))
                    _particlesData.Lifespan = generator.RequireType<IGeneratorNum>();

                break;
            case "fadein": //num or generator
                if (value.TryGetValueAsInteger(out int intValue))
                    _particlesData.FadeIn = new GeneratorNum(intValue);
                else if (value.TryGetValueAsDreamObject<DreamObjectGenerator>(out DreamObjectGenerator? generator))
                    _particlesData.FadeIn = generator.RequireType<IGeneratorNum>();

                break;
            case "fade": //num or generator
                if (value.TryGetValueAsInteger(out intValue))
                    _particlesData.FadeOut = new GeneratorNum(intValue);
                else if (value.TryGetValueAsDreamObject<DreamObjectGenerator>(out DreamObjectGenerator? generator))
                    _particlesData.FadeOut = generator.RequireType<IGeneratorNum>();

                break;
            case "position": //num, list, vector, or generator
                if (value.TryGetValueAsFloat(out floatValue)) {
                    _particlesData.SpawnPosition = new GeneratorNum(floatValue);
                } else if (value.TryGetValueAsDreamObject<DreamObjectGenerator>(out DreamObjectGenerator? generator)) {
                    _particlesData.SpawnPosition = generator.RequireType<IGeneratorVector>();
                } else if (DreamObjectVector.TryCreateFromValue(value, ObjectTree, out DreamObjectVector? vector)) {
                    _particlesData.SpawnPosition = new GeneratorVector2(vector.AsVector2);
                    vector.DecRef();
                } else {
                    _particlesData.SpawnPosition = new GeneratorVector2(Vector2.Zero);
                }

                break;
            case "velocity": //num, list, vector, or generator
                if (value.TryGetValueAsFloat(out floatValue)) {
                    _particlesData.SpawnVelocity = new GeneratorNum(floatValue);
                } else if (value.TryGetValueAsDreamObject<DreamObjectGenerator>(out DreamObjectGenerator? generator)) {
                    _particlesData.SpawnVelocity = generator.RequireType<IGeneratorVector>();
                } else if (DreamObjectVector.TryCreateFromValue(value, ObjectTree, out DreamObjectVector? vector)) {
                    _particlesData.SpawnVelocity = new GeneratorVector2(vector.AsVector2);
                    vector.DecRef();
                } else {
                    _particlesData.SpawnVelocity = new GeneratorVector2(Vector2.Zero);
                }

                break;
            case "scale": //num, list, vector, or generator
                if (value.TryGetValueAsFloat(out floatValue)) {
                    _particlesData.Scale = new GeneratorNum(floatValue);
                } else if (value.TryGetValueAsDreamObject<DreamObjectGenerator>(out DreamObjectGenerator? generator)) {
                    _particlesData.Scale = generator.RequireType<IGeneratorVector>();
                } else if (DreamObjectVector.TryCreateFromValue(value, ObjectTree, out DreamObjectVector? vector)) {
                    _particlesData.Scale = new GeneratorVector2(vector.AsVector2);
                    vector.DecRef();
                } else {
                    _particlesData.Scale = new GeneratorVector2(Vector2.One);
                }

                break;
            case "grow": //num, list, vector, or generator
                if (value.TryGetValueAsFloat(out floatValue)) {
                    _particlesData.Growth = new GeneratorNum(floatValue);
                } else if (value.TryGetValueAsDreamObject<DreamObjectGenerator>(out DreamObjectGenerator? generator)) {
                    _particlesData.Growth = generator.RequireType<IGeneratorVector>();
                } else if (DreamObjectVector.TryCreateFromValue(value, ObjectTree, out DreamObjectVector? vector)) {
                    _particlesData.Growth = new GeneratorVector2(vector.AsVector2);
                    vector.DecRef();
                } else {
                    _particlesData.Growth = new GeneratorVector2(Vector2.Zero);
                }

                break;
            case "rotation": //num or generator
                if (value.TryGetValueAsFloat(out floatValue))
                    _particlesData.Rotation = new GeneratorNum(floatValue);
                else if (value.TryGetValueAsDreamObject<DreamObjectGenerator>(out DreamObjectGenerator? generator))
                    _particlesData.Rotation = generator.RequireType<IGeneratorNum>();

                break;
            case "spin": //num or generator
                if (value.TryGetValueAsFloat(out floatValue))
                    _particlesData.Spin = new GeneratorNum(floatValue);
                else if (value.TryGetValueAsDreamObject<DreamObjectGenerator>(out DreamObjectGenerator? generator))
                    _particlesData.Spin = generator.RequireType<IGeneratorNum>();

                break;
            case "friction": //num, vector, or generator
                if (value.TryGetValueAsFloat(out floatValue)) {
                    _particlesData.Friction = new GeneratorNum(floatValue);
                } else if (value.TryGetValueAsDreamObject<DreamObjectGenerator>(out DreamObjectGenerator? generator)) {
                    _particlesData.Friction = generator.RequireType<IGeneratorVector>();
                } else if (DreamObjectVector.TryCreateFromValue(value, ObjectTree, out DreamObjectVector? vector)) {
                    _particlesData.Friction = new GeneratorVector2(vector.AsVector2);
                    vector.DecRef();
                } else {
                    _particlesData.Friction = new GeneratorVector2(Vector2.Zero);
                }

                break;
            case "drift": //num, vector, or generator
                if (value.TryGetValueAsFloat(out floatValue)) {
                    _particlesData.Drift = new GeneratorNum(floatValue);
                } else if (value.TryGetValueAsDreamObject<DreamObjectGenerator>(out DreamObjectGenerator? generator)) {
                    _particlesData.Drift = generator.RequireType<IGeneratorVector>();
                } else if (DreamObjectVector.TryCreateFromValue(value, ObjectTree, out DreamObjectVector? vector)) {
                    _particlesData.Drift = new GeneratorVector2(vector.AsVector2);
                    vector.DecRef();
                } else {
                    _particlesData.Drift = new GeneratorVector2(Vector2.Zero);
                }

                break;
        }

        //doing this here is fine 99% of the time since we're usually going to be changing particle vars
        MarkDirty(); //but this is a potentially redundant call
        base.SetVar(varName, value); //all calls should set the internal vars, so GetVar() can just be default also
    }
}
