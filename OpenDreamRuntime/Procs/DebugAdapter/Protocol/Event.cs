using System.Text.Json.Serialization;

namespace OpenDreamRuntime.Procs.DebugAdapter.Protocol;

public sealed class Event : ProtocolMessage, IEvent {
    public Event(string eventName, object? body = null) : base("event") {
        EventName = eventName;
        Body = body;
    }

    [JsonPropertyName("event")] public string EventName { get; set; }
    [JsonPropertyName("body")] public object? Body { get; set; }

    Event IEvent.ToEvent() {
        return this;
    }
}

public interface IEvent {
    Event ToEvent();
}
