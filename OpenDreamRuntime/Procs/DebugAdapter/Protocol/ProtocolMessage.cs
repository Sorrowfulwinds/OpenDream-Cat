using System.Text.Json.Serialization;
using JetBrains.Annotations;

namespace OpenDreamRuntime.Procs.DebugAdapter.Protocol;

[Virtual]
public class ProtocolMessage {
    [UsedImplicitly]
    public ProtocolMessage() {
    }

    protected ProtocolMessage(string type) {
        Type = type;
    }

    [JsonPropertyName("seq")] public int Seq { get; set; }
    [JsonPropertyName("type")] public string Type { get; set; } = null!;
}
