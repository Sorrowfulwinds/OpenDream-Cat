using OpenDreamShared.Dream;

namespace OpenDreamRuntime.Objects.Types;

public sealed class DreamObjectFilter(DreamObjectDefinition objectDefinition) : DreamObject(objectDefinition) {
    public static readonly Dictionary<DreamFilter, DreamFilterList> FilterAttachedTo = new();

    public DreamFilter Filter;

    public override bool ShouldCallNew => false;

    protected override void HandleDeletion() {
        FilterAttachedTo.Remove(Filter);
        base.HandleDeletion();
    }

    // TODO: Variable getting

    protected override void SetVar(string varName, DreamValue value) {
        if (FilterAttachedTo.TryGetValue(Filter, out DreamFilterList? attachedTo)) {
            int index = attachedTo.GetIndexOfFilter(Filter);

            DreamFilter? newFilter = DreamFilterHelpers.SetVar(Filter, varName, value);
            if (newFilter != null) {
                Filter = newFilter;
                attachedTo.SetFilter(index, newFilter);
            }
        }
    }

    public static DreamObjectFilter? TryCreateFilter(DreamObjectTree objectTree,
        IEnumerable<(string Name, DreamValue Value)> properties) {
        Type? filterType = null;
        var propertyList = new List<(string Name, DreamValue Value)>();

        foreach ((string Name, DreamValue Value) property in properties) {
            if (property.Value.IsNull)
                continue;

            if (property.Name == "type" && property.Value.TryGetValueAsString(out string? filterTypeName))
                filterType = DreamFilter.GetType(filterTypeName);

            propertyList.Add(property);
        }

        if (filterType == null)
            return null;

        DreamFilter filter = DreamFilterHelpers.Create(filterType, propertyList);

        var filterObject = objectTree.CreateObject<DreamObjectFilter>(objectTree.Filter);
        filterObject.Filter = filter;
        return filterObject;
    }

    public static DreamObjectFilter? TryCreateFilter(DreamObjectTree objectTree, DreamList list) {
        static IEnumerable<(string, DreamValue)> EnumerateProperties(DreamList list) {
            foreach (DreamValue key in list.EnumerateValues()) {
                if (!key.TryGetValueAsString(out string? keyStr))
                    continue;

                using DreamValue value = list.GetValue(key);
                if (value.IsNull)
                    continue;

                yield return (keyStr, value);
            }
        }

        return TryCreateFilter(objectTree, EnumerateProperties(list));
    }
}
