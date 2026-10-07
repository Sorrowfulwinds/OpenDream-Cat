using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading;
using OpenDreamRuntime.Objects.Types;
using OpenDreamShared.Dream;
using OpenDreamShared.Network.Messages;
using Robust.Server.Player;
using Robust.Shared.Enums;
using Robust.Shared.Map;
using Robust.Shared.Player;
using Robust.Shared.Serialization;
using SharedAppearanceSystem = OpenDreamShared.Rendering.SharedAppearanceSystem;

namespace OpenDreamRuntime.Rendering;

public sealed partial class ServerAppearanceSystem : SharedAppearanceSystem {
    /// <summary>
    ///     Each appearance gets a unique ID when marked as registered. Here we store these as a key -> weakref in a weaktable,
    ///     which does not count
    ///     as a hard ref but allows quick lookup. Each object which holds an appearance MUST hold that ImmutableAppearance
    ///     until it is no longer
    ///     needed or it will be GC'd. Overlays & underlays are stored as hard refs on the ImmutableAppearance so you only need
    ///     to hold the main appearance.
    /// </summary>
    private readonly HashSet<ProxyWeakRef> _appearanceLookup = new();

    private readonly Queue<uint> _appearanceRemovalQueue = new();
    private readonly List<ImmutableAppearance> _appearanceSendQueue = new();
    private readonly Dictionary<uint, ProxyWeakRef> _idToAppearance = new();

    /// <summary>
    ///     This system is used by the PVS thread, we need to be thread-safe
    /// </summary>
    private readonly Lock _lock = new();

    public ImmutableAppearance DefaultAppearance = default!;
    [Dependency] private AtomManager _atomManager = default!;
    private uint _counter;

    [Dependency] private DreamManager _dreamManager = default!;
    [Dependency] private IPlayerManager _playerManager = default!;
    [Dependency] private IRobustSerializer _serializer = default!;

    public override void Initialize() {
        UpdatesOutsidePrediction = true;

        DefaultAppearance = new ImmutableAppearance(MutableAppearance.Default, this);
        DefaultAppearance
            .MarkRegistered(_counter++); //first appearance registered gets id 0, this is the blank default appearance
        ProxyWeakRef proxyWeakRef = new(DefaultAppearance);
        _appearanceLookup.Add(proxyWeakRef);
        _idToAppearance.Add(DefaultAppearance.MustGetId(), proxyWeakRef);
        //leaving this in as a sanity check for mutable and immutable appearance hashcodes covering all the same vars
        //if this debug assert fails, you've probably changed appearance var and not updated its counterpart
        Debug.Assert(DefaultAppearance.GetHashCode() == MutableAppearance.Default.GetHashCode());

        _playerManager.PlayerStatusChanged += OnPlayerStatusChanged;
    }

    public override void Shutdown() {
        lock (_lock) {
            _appearanceLookup.Clear();
            _idToAppearance.Clear();
        }
    }

    public override void Update(float frameTime) {
        lock (_lock) {
            if (_appearanceSendQueue.Count > 0) {
                using MemoryStream compressed = MsgAllAppearances.CompressAppearances(_appearanceSendQueue,
                    _appearanceSendQueue.Count, _serializer);

                RaiseNetworkEvent(new NewAppearancesEvent(compressed.ToArray()));
                _appearanceSendQueue.Clear();
            }

            if (_appearanceRemovalQueue.Count > 0) {
                var removalEvent = new RemoveAppearancesEvent(_appearanceRemovalQueue.ToArray());
                RaiseNetworkEvent(removalEvent);

                while (_appearanceRemovalQueue.TryDequeue(out uint appearanceId)) {
                    ProxyWeakRef proxyWeakRef = _idToAppearance[appearanceId];

                    proxyWeakRef.TryGetTarget(out ImmutableAppearance? appearance);
                    if (_appearanceLookup.TryGetValue(proxyWeakRef, out ProxyWeakRef? weakRef)) {
                        //it is possible that a new appearance was created with the same hash before the GC got around to cleaning up the old one
                        if (weakRef.TryGetTarget(out ImmutableAppearance? target) &&
                            !ReferenceEquals(target, appearance))
                            continue;

                        _appearanceLookup.Remove(proxyWeakRef);
                        _idToAppearance.Remove(appearanceId);
                    }
                }
            }
        }

        base.Update(frameTime);
    }

    private void OnPlayerStatusChanged(object? sender, SessionStatusEventArgs e) {
        if (e.NewStatus == SessionStatus.InGame)
            //todo this is probably stupid slow
            lock (_lock) {
                Dictionary<uint, ImmutableAppearance> sendData = new(_appearanceLookup.Count);

                foreach (ProxyWeakRef proxyWeakRef in _appearanceLookup)
                    if (proxyWeakRef.TryGetTarget(out ImmutableAppearance? immutable))
                        sendData.Add(immutable.MustGetId(), immutable);

                Logger.GetSawmill("appearance")
                    .Debug($"Sending {sendData.Count} appearances to new player {e.Session.Name}");
                e.Session.Channel.SendMessage(new MsgAllAppearances(sendData));
            }
    }

    private void RegisterAppearance(ImmutableAppearance immutableAppearance) {
        immutableAppearance
            .MarkRegistered(_counter++); //lets this appearance know it needs to do GC finaliser & get an ID

        ProxyWeakRef proxyWeakRef = new(immutableAppearance);
        _appearanceLookup.Add(proxyWeakRef);
        _idToAppearance.Add(immutableAppearance.MustGetId(), proxyWeakRef);
        _appearanceSendQueue.Add(immutableAppearance);
    }

    public ImmutableAppearance AddAppearance(MutableAppearance appearance, bool registerAppearance = true) {
        ImmutableAppearance immutableAppearance = new(appearance, this);

        return AddAppearance(immutableAppearance, registerAppearance);
    }

    public ImmutableAppearance AddAppearance(ImmutableAppearance appearance, bool registerAppearance = true) {
        lock (_lock) {
            if (_appearanceLookup.TryGetValue(new ProxyWeakRef(appearance), out ProxyWeakRef? weakReference) &&
                weakReference.TryGetTarget(out ImmutableAppearance? originalImmutable)) return originalImmutable;

            if (registerAppearance) {
                RegisterAppearance(appearance);
                return appearance;
            }

            return appearance;
        }
    }

    //this should only be called by the ImmutableAppearance's finalizer
    [Access(typeof(ImmutableAppearance))]
    public override void RemoveAppearance(ImmutableAppearance appearance) {
        lock (_lock) {
            _appearanceRemovalQueue.Enqueue(appearance.MustGetId());
        }
    }

    public override ImmutableAppearance MustGetAppearanceById(uint appearanceId) {
        lock (_lock) {
            if (!_idToAppearance[appearanceId].TryGetTarget(out ImmutableAppearance? result))
                throw new Exception(
                    $"Attempted to access deleted appearance ID ${appearanceId} in MustGetAppearanceByID()");
            return result;
        }
    }

    public bool TryGetAppearanceById(uint appearanceId, [NotNullWhen(true)] out ImmutableAppearance? appearance) {
        lock (_lock) {
            appearance = null;
            return _idToAppearance.TryGetValue(appearanceId, out ProxyWeakRef? appearanceRef) &&
                   appearanceRef.TryGetTarget(out appearance);
        }
    }

    public void Animate(EntityUid entity, MutableAppearance targetAppearance, TimeSpan duration, AnimationEasing easing,
        int loop, AnimationFlags flags, int delay, bool chainAnim, uint? turfId) {
        uint appearanceId = AddAppearance(targetAppearance).MustGetId();
        NetEntity netEntity = GetNetEntity(entity);
        var animateEvent = new AnimationEvent(netEntity, appearanceId, duration, easing, loop, flags, delay, chainAnim,
            turfId);

        if (entity.IsValid())
            RaiseNetworkEvent(animateEvent, Filter.Pvs(entity));
        else
            RaiseNetworkEvent(animateEvent); // TODO: Filter for non-entities
    }

    public void Flick(DreamObjectAtom atom, int iconId, string? iconState) {
        (int X, int Y, int Z) position = _atomManager.GetAtomPosition(atom);
        var mapCoords = new MapCoordinates(position.X, position.Y, new MapId(position.Z));
        ClientObjectReference clientRef = _dreamManager.GetClientReference(atom);
        var flickEvent = new FlickEvent(clientRef, iconId, iconState);

        RaiseNetworkEvent(flickEvent, Filter.Pvs(mapCoords));
    }
}

//this class lets us hold a weakref and also do quick lookups in hash tables
internal sealed class ProxyWeakRef : IEquatable<ProxyWeakRef> {
    private readonly int _hashCode;
    private readonly uint? _registeredId;

    private readonly WeakReference<ImmutableAppearance> _weakRef;

    public ProxyWeakRef(ImmutableAppearance appearance) {
        appearance.TryGetId(out _registeredId);
        _weakRef = new WeakReference<ImmutableAppearance>(appearance);
        _hashCode = appearance.GetHashCode();
    }

    public bool Equals(ProxyWeakRef? proxy) {
        if (proxy is null)
            return false;
        if (_registeredId is not null && _registeredId == proxy._registeredId)
            return true;
        if (_weakRef.TryGetTarget(out ImmutableAppearance? thisRef) &&
            proxy._weakRef.TryGetTarget(out ImmutableAppearance? thatRef))
            return thisRef.Equals(thatRef);
        return false;
    }

    public bool TryGetTarget([NotNullWhen(true)] out ImmutableAppearance? target) {
        return _weakRef.TryGetTarget(out target);
    }

    public override int GetHashCode() {
        return _hashCode;
    }

    public override bool Equals(object? obj) {
        return obj is ProxyWeakRef proxy && Equals(proxy);
    }
}
