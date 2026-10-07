namespace OpenDreamPackageTool;

public record PlatformReg(string RId, string TargetOs, bool BuildByDefault) {
    public bool BuildByDefault = BuildByDefault;
    public string RId = RId;
    public string TargetOs = TargetOs;
}
