using OpenDreamShared.Dream;

namespace OpenDreamRuntime.Objects.Types;

[Virtual]
public class DreamObjectAtom(DreamObjectDefinition objectDefinition) : DreamObject(objectDefinition) {
    private DreamFilterList? _filters;

    private DreamOverlaysList? _overlays;
    private DreamOverlaysList? _underlays;
    private DreamVisContentsList? _visContents;
    private DreamList? _visLocs; // TODO: Implement

    private DreamOverlaysList Overlays => _overlays ??=
        new DreamOverlaysList(ObjectTree.List.ObjectDefinition, this, AppearanceSystem, false);

    private DreamOverlaysList Underlays => _underlays ??=
        new DreamOverlaysList(ObjectTree.List.ObjectDefinition, this, AppearanceSystem, true);

    private DreamVisContentsList VisContents => _visContents ??=
        new DreamVisContentsList(ObjectTree.List.ObjectDefinition, PvsOverrideSystem, this);

    private DreamFilterList Filters => _filters ??= new DreamFilterList(ObjectTree.List.ObjectDefinition, this);
    private DreamList VisLocs => _visLocs ??= ObjectTree.CreateList();

    protected string GetRTEntityDesc() {
        if (AtomManager.TryGetAppearance(this, out ImmutableAppearance? appearance) && appearance.Desc != null)
            return appearance.Desc;

        return ObjectDefinition.Type;
    }

    protected override void HandleDeletion() {
        _overlays?.DecRef();
        _underlays?.DecRef();
        _visContents?.DecRef();
        _filters?.DecRef();
        _visLocs?.DecRef();

        base.HandleDeletion();
    }

    protected override bool TryGetVar(string varName, out DreamValue value) {
        switch (varName) {
            // x/y/z/loc should be overriden by subtypes
            case "x":
            case "y":
            case "z":
                value = new DreamValue(0);
                return true;
            case "loc":
                value = DreamValue.Null;
                return true;
            case "appearance":
                MutableAppearance appearanceCopy = AtomManager.MustGetAppearance(this).ToMutable();

                value = new DreamValue(appearanceCopy);
                return true;
            case "overlays":
                Overlays.IncRef();
                value = new DreamValue(Overlays);
                return true;
            case "underlays":
                Underlays.IncRef();
                value = new DreamValue(Underlays);
                return true;
            case "verbs":
                value = new DreamValue(new VerbsList(ObjectTree, AtomManager, this));
                return true;
            case "filters":
                Filters.IncRef();
                value = new DreamValue(Filters);
                return true;
            case "vis_locs":
                VisLocs.IncRef();
                value = new DreamValue(VisLocs);
                return true;
            case "vis_contents":
                VisContents.IncRef();
                value = new DreamValue(VisContents);
                return true;

            default:
                if (AtomManager.IsValidAppearanceVar(varName)) {
                    ImmutableAppearance appearance = AtomManager.MustGetAppearance(this);

                    value = AtomManager.GetAppearanceVar(appearance, varName);
                    return true;
                }

                return base.TryGetVar(varName, out value);
        }
    }

    protected override void SetVar(string varName, DreamValue value) {
        switch (varName) {
            // x/y/z/loc should be overriden by subtypes
            case "x":
            case "y":
            case "z":
            case "loc":
                break;
            case "appearance":
                if (!AtomManager.TryCreateAppearanceFrom(value, out MutableAppearance? newAppearance))
                    return; // Ignore attempts to set an invalid appearance

                // The dir does not get changed
                newAppearance.Direction = AtomManager.MustGetAppearance(this).Direction;

                AtomManager.SetAtomAppearance(this, newAppearance);
                newAppearance.Dispose();
                break;
            case "overlays": {
                Overlays.Cut();

                if (value.TryGetValueAsDreamList(out DreamList? valueList))
                    // TODO: This should postpone UpdateAppearance until after everything is added
                    foreach (DreamValue overlayValue in valueList.EnumerateValues())
                        Overlays.AddValue(overlayValue);
                else if (!value.IsNull) Overlays.AddValue(value);

                break;
            }
            case "underlays": {
                Underlays.Cut();

                if (value.TryGetValueAsDreamList(out DreamList? valueList))
                    // TODO: This should postpone UpdateAppearance until after everything is added
                    foreach (DreamValue underlayValue in valueList.EnumerateValues())
                        Underlays.AddValue(underlayValue);
                else if (!value.IsNull) Underlays.AddValue(value);

                break;
            }
            case "vis_contents": {
                VisContents.Cut();

                if (value.TryGetValueAsDreamList(out DreamList? valueList))
                    // TODO: This should postpone UpdateAppearance until after everything is added
                    foreach (DreamValue visContentsValue in valueList.EnumerateValues())
                        VisContents.AddValue(visContentsValue);
                else if (!value.IsNull) VisContents.AddValue(value);

                break;
            }
            case "filters": {
                Filters.Cut();

                // filters = list("type"=...) or list(filter(...), filter(...))
                if (value.TryGetValueAsDreamList(out DreamList? valueList)) {
                    using DreamValue typeArg = valueList.GetValue(new DreamValue("type"));

                    if (typeArg != DreamValue.Null) { // It's a single filter
                        var filterObject = DreamObjectFilter.TryCreateFilter(ObjectTree, valueList);
                        if (filterObject == null) // list() with invalid "type" is ignored
                            break;

                        Filters.AddValue(new DreamValue(filterObject));
                        filterObject.DecRef();
                    } else { // It's a list of filters
                        foreach (DreamValue filter in valueList.EnumerateValues()) {
                            if (!filter.TryGetValueAsDreamObject(
                                    out DreamObjectFilter? filterObject)) {
                                if (!filter.TryGetValueAsDreamList(out DreamList? filterValues))
                                    continue;

                                filterObject = DreamObjectFilter.TryCreateFilter(ObjectTree, filterValues);
                                if (filterObject == null)
                                    continue;
                            }

                            Filters.AddValue(new DreamValue(filterObject));
                            filterObject.DecRef();
                        }
                    }
                } else if (!value.IsNull) {
                    Filters.AddValue(value);
                }

                break;
            }
            default:
                if (AtomManager.IsValidAppearanceVar(varName)) {
                    // Basically AtomManager.UpdateAppearance() but without the performance impact of using actions
                    using MutableAppearance appearance = AtomManager.MustGetAppearance(this).ToMutable();
                    AtomManager.SetAppearanceVar(appearance, varName, value);
                    AtomManager.SetAtomAppearance(this, appearance);
                    break;
                }

                base.SetVar(varName, value);
                break;
        }
    }
}
