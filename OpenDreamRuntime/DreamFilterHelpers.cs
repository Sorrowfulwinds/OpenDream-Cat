using System.Reflection;
using OpenDreamRuntime.Objects.Types;
using OpenDreamRuntime.Procs.Native;
using OpenDreamRuntime.Resources;
using OpenDreamShared.Dream;

namespace OpenDreamRuntime;

/// <summary>
///     Converts DM filter vars (DreamValues) directly onto <see cref="DreamFilter" /> classes.
/// </summary>
public static class DreamFilterHelpers {
    private static readonly Dictionary<Type, Dictionary<string, (FieldInfo Field, bool Required)>> FieldsByType =
        BuildFieldsByType();

    private static Dictionary<Type, Dictionary<string, (FieldInfo Field, bool Required)>> BuildFieldsByType() {
        var cache = new Dictionary<Type, Dictionary<string, (FieldInfo Field, bool Required)>>();
        foreach (Type type in DreamFilter.AllTypes) cache[type] = BuildFields(type);

        return cache;
    }

    private static Dictionary<string, (FieldInfo Field, bool Required)> BuildFields(Type type) {
        var fields = new Dictionary<string, (FieldInfo, bool)>();
        for (Type? t = type; t != null && t != typeof(object); t = t.BaseType)
            foreach (FieldInfo field in t.GetFields(BindingFlags.Public | BindingFlags.Instance)) {
                var dataField = field.GetCustomAttribute<DataFieldAttribute>();
                if (dataField?.Tag == null)
                    continue;

                fields[dataField.Tag] = (field, dataField.Required);
            }

        return fields;
    }

    private static Dictionary<string, (FieldInfo Field, bool Required)> GetFields(Type filterType) {
        return FieldsByType[filterType];
    }

    /// <summary>
    ///     Creates a new filter of the given type, applying defaults from field initializers,
    ///     setting the provided vars, and enforcing required fields.
    /// </summary>
    public static DreamFilter Create(Type filterType, IEnumerable<(string Name, DreamValue Value)> vars) {
        var filter = (DreamFilter)Activator.CreateInstance(filterType)!;
        var provided = new HashSet<string>();

        foreach ((string name, DreamValue value) in vars)
            if (SetField(filter, name, value))
                provided.Add(name);

        foreach ((string name, (FieldInfo Field, bool Required) field) in GetFields(filterType))
            if (field.Required && !provided.Contains(name))
                throw new Exception($"Filter type \"{filter.FilterType}\" requires a value for \"{name}\"");

        return filter;
    }

    /// <summary>
    ///     Returns a copy of the filter with the given var changed, or null if the var is unknown
    ///     or the value didn't change. Copy-on-write, so appearances sharing this filter instance
    ///     are not affected.
    /// </summary>
    public static DreamFilter? SetVar(DreamFilter filter, string varName, DreamValue value) {
        Dictionary<string, (FieldInfo Field, bool Required)> fields = GetFields(filter.GetType());
        if (!fields.TryGetValue(varName, out (FieldInfo Field, bool Required) fieldInfo))
            return null;

        object converted = Convert(value, fieldInfo.Field.FieldType, varName);
        if (Equals(fieldInfo.Field.GetValue(filter), converted))
            return null;

        var newFilter = (DreamFilter)Activator.CreateInstance(filter.GetType())!;
        foreach ((string name, (FieldInfo Field, bool Required) other) in fields) {
            if (name == varName)
                continue;

            other.Field.SetValue(newFilter, other.Field.GetValue(filter));
        }

        fieldInfo.Field.SetValue(newFilter, converted);
        return newFilter;
    }

    private static bool SetField(DreamFilter filter, string varName, DreamValue value) {
        Dictionary<string, (FieldInfo Field, bool Required)> fields = GetFields(filter.GetType());
        if (!fields.TryGetValue(varName, out (FieldInfo Field, bool Required) fieldInfo))
            return false;

        fieldInfo.Field.SetValue(filter, Convert(value, fieldInfo.Field.FieldType, varName));
        return true;
    }

    private static object Convert(DreamValue value, Type fieldType, string varName) {
        if (fieldType == typeof(float)) {
            if (value.TryGetValueAsFloat(out float floatValue))
                return floatValue;

            throw new Exception($"Value {value} is not a float");
        }

        if (fieldType == typeof(int)) {
            if (varName == "icon") {
                var resourceManager = IoCManager.Resolve<DreamResourceManager>();
                if (!resourceManager.TryLoadIcon(value, out IconResource? icon))
                    throw new Exception($"Value {value} is not a valid IconResource type");

                return icon.Id;
            }

            if (value.TryGetValueAsInteger(out int intValue))
                return intValue;

            throw new Exception($"Value {value} is not an integer");
        }

        if (fieldType == typeof(short)) {
            if (value.TryGetValueAsInteger(out int intValue))
                return (short)intValue;

            throw new Exception($"Value {value} is not an integer");
        }

        if (fieldType == typeof(string)) {
            if (value.TryGetValueAsString(out string? stringValue))
                return stringValue;

            throw new Exception($"Value {value} is not a string");
        }

        if (fieldType == typeof(Color)) {
            if (value.TryGetValueAsString(out string? colorString) &&
                ColorHelpers.TryParseColor(colorString, out Color color))
                return color;

            throw new Exception($"Value {value} is not a color");
        }

        if (fieldType == typeof(ColorMatrix)) {
            if (value.TryGetValueAsString(out string? maybeColorString)) {
                if (ColorHelpers.TryParseColor(maybeColorString, out Color basicColor))
                    return new ColorMatrix(basicColor);
            } else if (value.TryGetValueAsDreamList(out DreamList? matrixList)) {
                if (DreamProcNativeHelpers.TryParseColorMatrix(matrixList, out ColorMatrix matrix))
                    return matrix;
            }

            throw new Exception($"Value {value} is not a color matrix");
        }

        if (fieldType == typeof(Matrix3x2)) {
            if (value.TryGetValueAsDreamObject(out DreamObjectMatrix? matrixObject))
                return new Matrix3x2(
                    matrixObject.A, matrixObject.D,
                    matrixObject.B, matrixObject.E,
                    matrixObject.C, matrixObject.F);

            throw new Exception($"Value {value} is not a matrix");
        }

        throw new Exception($"Unsupported filter field type {fieldType}");
    }
}
