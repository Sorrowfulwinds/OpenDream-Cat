namespace OpenDreamRuntime.Procs;

[AttributeUsage(AttributeTargets.Method)]
internal sealed class DreamProcAttribute : Attribute {
    public string Name;

    public DreamProcAttribute(string name) {
        Name = name;
    }
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
internal sealed class DreamProcParameterAttribute : Attribute {
    public object? DefaultValue;
    public string Name;
    public DreamValue.DreamValueTypeFlag Type;

    public DreamProcParameterAttribute(string name) {
        Name = name;
    }
}
