using OpenDreamRuntime.Map;
using OpenDreamShared.Dream;

namespace OpenDreamRuntime.Objects.Types;

public sealed class DreamObjectArea : DreamObjectAtom {
    public readonly HashSet<DreamObjectTurf> Turfs;

    private readonly AreaContentsList _contents;

    public ImmutableAppearance Appearance;

    // Iterating all our turfs to find the one with the lowest coordinates is slow business
    private int? _cachedX, _cachedY, _cachedZ;

    public DreamObjectArea(DreamObjectDefinition objectDefinition) : base(objectDefinition) {
        Appearance = AppearanceSystem!.DefaultAppearance;
        Turfs = new HashSet<DreamObjectTurf>();
        _contents = new AreaContentsList(ObjectTree.List.ObjectDefinition, this);
        AtomManager.SetAtomAppearance(this, AtomManager.GetAppearanceFromDefinition(ObjectDefinition));
    }

    // Areas are reference counted, but BYOND never garbage collects them
    public override bool ShouldGarbageCollect => false;

    public int X {
        get {
            UpdateCoordinateCache();
            return _cachedX!.Value;
        }
    }

    public int Y {
        get {
            UpdateCoordinateCache();
            return _cachedY!.Value;
        }
    }

    public int Z {
        get {
            UpdateCoordinateCache();
            return _cachedZ!.Value;
        }
    }

    protected override void HandleDeletion() {
        _contents.DecRef();
        _contents.Delete();
        base.HandleDeletion();
    }

    /// <summary>
    ///     Forces us to find the up-to-date "lowest" turf on next coordinate var access
    /// </summary>
    public void ResetCoordinateCache() {
        _cachedX = _cachedY = _cachedZ = null;
    }

    protected override bool TryGetVar(string varName, out DreamValue value) {
        switch (varName) {
            case "x":
                value = new DreamValue(X);
                return true;
            case "y":
                value = new DreamValue(Y);
                return true;
            case "z":
                value = new DreamValue(Z);
                return true;
            case "contents":
                _contents.IncRef();
                value = new DreamValue(_contents);
                return true;
            default:
                return base.TryGetVar(varName, out value);
        }
    }

    protected override void SetVar(string varName, DreamValue value) {
        switch (varName) {
            case "x":
            case "y":
            case "z":
                throw new DMException($"Cannot set coordinate var '{varName}' on an area");
            case "contents":
                // TODO
                break;
            default:
                base.SetVar(varName, value);
                break;
        }
    }

    public override void OperatorOutput(DreamValue b) {
        if (b.TryGetValueAsDreamObject<DreamObjectSound>(out _)) {
            // Output the sound to every connection with a mob inside this area
            foreach (DreamConnection connection in DreamManager.Connections) {
                DreamObjectMob? mob = connection.Mob;
                if (mob == null)
                    continue;

                if (!DreamMapManager.TryGetCellAt(mob.Position, mob.Z, out IDreamMapManager.Cell? cell))
                    continue;

                if (cell.Area != this)
                    continue;

                connection.OutputDreamValue(b);
            }

            return;
        }

        base.OperatorOutput(b);
    }

    /// <summary>
    ///     Updates our cached coordinates with the location of the "lowest" turf, if we don't already have them cached.
    ///     <br />
    ///     The "lowest" turf is the turf with the lowest z, y, then x.
    /// </summary>
    private void UpdateCoordinateCache() {
        if (_cachedX != null)
            return;

        foreach (DreamObjectTurf turf in Turfs) {
            if (_cachedX != null) {
                if (turf.Z > _cachedZ)
                    continue;

                int index = turf.Y * DreamMapManager.Size.X + turf.X;
                if (index >= _cachedY * DreamMapManager.Size.X + _cachedX)
                    continue;
            }

            _cachedX = turf.X;
            _cachedY = turf.Y;
            _cachedZ = turf.Z;
        }

        // 0 if there were no turfs
        _cachedX ??= _cachedY = _cachedZ = 0;
    }
}
