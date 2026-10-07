using OpenDreamRuntime.Procs.Native;
using OpenDreamShared.Rendering;

namespace OpenDreamRuntime.Objects.Types;

public sealed class DreamObjectMob : DreamObjectMovable {
    private readonly DreamMobSightComponent _sightComponent;
    public DreamConnection? Connection;
    public string? Key;

    public DreamObjectMob(DreamObjectDefinition objectDefinition) : base(objectDefinition) {
        _sightComponent = EntityManager.AddComponent<DreamMobSightComponent>(Entity);

        objectDefinition.Variables["see_invisible"].TryGetValueAsInteger(out int seeVis);
        objectDefinition.Variables["sight"].TryGetValueAsInteger(out int sight);

        SeeInvisible = seeVis;
        Sight = (SightFlags)sight;
    }

    // DM specific behavior, do not garbage collect mobs that have a key.
    public override bool ShouldGarbageCollect => string.IsNullOrEmpty(Key);

    public int SeeInvisible {
        get => _sightComponent.SeeInvisibility;
        private set {
            _sightComponent.SeeInvisibility = (sbyte)value;
            EntityManager.Dirty(Entity, _sightComponent);
        }
    }

    public SightFlags Sight {
        get => _sightComponent.Sight;
        private set {
            _sightComponent.Sight = value;
            EntityManager.Dirty(Entity, _sightComponent);
        }
    }

    protected override bool TryGetVar(string varName, out DreamValue value) {
        switch (varName) {
            case "client":
                Connection?.Client?.IncRef();
                value = new DreamValue(Connection?.Client);
                return true;
            case "key":
                value = Key != null ? new DreamValue(Key) : DreamValue.Null;
                return true;
            case "ckey":
                value = Key != null ? new DreamValue(DreamProcNativeHelpers.Ckey(Key)) : DreamValue.Null;
                return true;
            case "see_invisible":
                value = new DreamValue(SeeInvisible);
                return true;
            case "sight":
                value = new DreamValue((int)Sight);
                return true;
            default:
                return base.TryGetVar(varName, out value);
        }
    }

    protected override void SetVar(string varName, DreamValue value) {
        switch (varName) {
            case "client":
                // An invalid client or a null does nothing here
                if (value.TryGetValueAsDreamObject<DreamObjectClient>(out DreamObjectClient? newClient))
                    newClient.Connection.Mob = this;

                break;
            // "key" uses ckey comparison when *assigning* according to docs so... Just make them use same code path
            // RobustToolbox auth allows usernames in form of a-z0-9_ so there can be a collision between User1 and User_1
            case "key":
            case "ckey":
                if (!value.TryGetValueAsString(out Key)) {
                    // TODO: Does the key get set to a player's un-canonized username?
                    Connection?.Mob = null;
                    break;
                }

                // Ensure "canonical" form
                Key = DreamProcNativeHelpers.Ckey(Key);

                foreach (DreamConnection connection in DreamManager.Connections)
                    if (DreamProcNativeHelpers.Ckey(connection.Key) == Key) {
                        connection.Mob = this;
                        break;
                    }

                break;
            case "see_invisible":
                value.TryGetValueAsInteger(out int seeVis);

                SeeInvisible = seeVis;
                break;
            case "sight":
                value.TryGetValueAsInteger(out int sight);

                Sight = (SightFlags)sight;
                break;
            default:
                base.SetVar(varName, value);
                break;
        }
    }

    public override void OperatorOutput(DreamValue b) {
        Connection?.OutputDreamValue(b);
    }
}
