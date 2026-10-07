using OpenDreamRuntime.Map;
using OpenDreamRuntime.Procs;
using OpenDreamShared.Dream;

namespace OpenDreamRuntime.Objects.Types;

public sealed class DreamObjectTurf : DreamObjectAtom {
    public readonly TurfContentsList Contents;
    public readonly int X, Y, Z;
    public ImmutableAppearance Appearance;
    public IDreamMapManager.Cell Cell;
    public bool IsDense;

    public DreamObjectTurf(DreamObjectDefinition objectDefinition, int x, int y, int z) : base(objectDefinition) {
        X = x;
        Y = y;
        Z = z;

        Cell = default!; // NEEDS to be set by DreamMapManager after creation
        Appearance = default!; // This will be set by DreamMapManager
        Contents = new TurfContentsList(ObjectTree.List.ObjectDefinition, this);
    }

    public override void Initialize(DreamProcArguments args) {
        base.Initialize(args);

        ObjectDefinition.TryGetVariable("density", out DreamValue density);
        IsDense = density.IsTruthy();
    }

    public void SetTurfType(DreamObjectDefinition objectDefinition) {
        if (!objectDefinition.IsSubtypeOf(ObjectTree.Turf))
            throw new DMException($"Cannot set turf's type to {objectDefinition.Type}");

        ObjectDefinition = objectDefinition;

        if (Variables != null) {
            foreach (DreamValue varValue in Variables.Values)
                varValue.DecRef();

            Variables?.Clear();
        }

        Initialize(new DreamProcArguments());
    }

    public void OnAreaChange(DreamObjectArea oldArea) {
        if (Cell == null!)
            return;

        using MutableAppearance newAppearance = Appearance.ToMutable();

        newAppearance.Overlays.Remove(oldArea.Appearance);
        DreamMapManager.SetTurfAppearance(this, newAppearance);
    }

    protected override void HandleDeletion() {
        Contents.DecRef();
        Contents.Delete();
        base.HandleDeletion();
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
            case "loc":
                Cell.Area.IncRef();
                value = new DreamValue(Cell.Area);
                return true;
            case "density":
                value = IsDense ? DreamValue.True : DreamValue.False;
                return true;
            case "contents":
                Contents.IncRef();
                value = new DreamValue(Contents);
                return true;
            default:
                return base.TryGetVar(varName, out value);
        }
    }

    protected override void SetVar(string varName, DreamValue value) {
        switch (varName) {
            case "density":
                IsDense = value.IsTruthy();
                break;
            case "contents":
                Contents.Cut();

                if (value.TryGetValueAsDreamList(out DreamList? valueList))
                    foreach (DreamValue contentValue in valueList.EnumerateValues())
                        Contents.AddValue(contentValue);

                break;
            default:
                base.SetVar(varName, value);
                break;
        }
    }
}
