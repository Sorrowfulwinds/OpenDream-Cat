namespace OpenDreamRuntime.Procs.DebugAdapter.Protocol;

public sealed class InitializedEvent : IEvent {
    Event IEvent.ToEvent() {
        return new Event("initialized");
    }
}
