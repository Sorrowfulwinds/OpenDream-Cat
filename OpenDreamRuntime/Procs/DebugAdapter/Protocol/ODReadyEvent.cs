using System.Text.Json.Serialization;

namespace OpenDreamRuntime.Procs.DebugAdapter.Protocol;

public sealed class ODReadyEvent : IEvent {
    public ODReadyEvent(int port) {
        Port = port;
    }

    [JsonPropertyName("gamePort")] public int Port { get; set; }

    Event IEvent.ToEvent() {
        return new Event("$opendream/ready", this);
    }
}
