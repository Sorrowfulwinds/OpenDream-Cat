using System.Threading.Tasks;
using OpenDreamClient.Interface;
using OpenDreamClient.Rendering;
using OpenDreamShared.Dream;
using OpenDreamShared.Rendering;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Client.Player;
using Robust.Shared.Asynchronous;

namespace OpenDreamClient;

public sealed partial class ClientVerbSystem : VerbSystem {
    private readonly Dictionary<int, VerbInfo> _verbs = new();
    private List<int>? _clientVerbs;
    [Dependency] private IEntityManager _entityManager = default!;
    [Dependency] private IDreamInterfaceManager _interfaceManager = default!;
    [Dependency] private IOverlayManager _overlayManager = default!;
    [Dependency] private IPlayerManager _playerManager = default!;
    private EntityQuery<DreamMobSightComponent> _sightQuery;
    private EntityQuery<DMISpriteComponent> _spriteQuery;
    [Dependency] private ITaskManager _taskManager = default!;
    [Dependency] private TransformSystem _transformSystem = default!;

    private EntityQuery<TransformComponent> _xformQuery;

    public override void Initialize() {
        _spriteQuery = _entityManager.GetEntityQuery<DMISpriteComponent>();
        _sightQuery = _entityManager.GetEntityQuery<DreamMobSightComponent>();
        _xformQuery = _entityManager.GetEntityQuery<TransformComponent>();

        SubscribeNetworkEvent<AllVerbsEvent>(OnAllVerbsEvent);
        SubscribeNetworkEvent<RegisterVerbEvent>(OnRegisterVerbEvent);
        SubscribeNetworkEvent<UpdateClientVerbsEvent>(OnUpdateClientVerbsEvent);
    }

    public override void Shutdown() {
        _verbs.Clear();
        _clientVerbs = null;
    }

    /// <summary>
    ///     Prompt the user for the arguments to a verb, then ask the server to execute it
    /// </summary>
    /// <param name="src">The target of the verb</param>
    /// <param name="verbId">ID of the verb to execute</param>
    public async void ExecuteVerb(ClientObjectReference src, int verbId) {
        VerbInfo verbInfo = _verbs[verbId];

        RaiseNetworkEvent(new ExecuteVerbEvent(src, verbId, await PromptVerbArguments(verbInfo)));
    }

    /// <summary>
    ///     Ask the server to execute a verb with the given arguments
    /// </summary>
    /// <param name="src">The target of the verb</param>
    /// <param name="verbId">ID of the verb to execute</param>
    /// <param name="arguments">Arguments to the verb</param>
    /// <remarks>The server will not execute the verb if the arguments are invalid</remarks>
    /// // TODO: I think the server actually just errors, fix that
    public void ExecuteVerb(ClientObjectReference src, int verbId, object?[] arguments) {
        RaiseNetworkEvent(new ExecuteVerbEvent(src, verbId, arguments));
    }

    public void StartRepeatingVerb(ClientObjectReference src, int verbId) {
        RaiseNetworkEvent(new RegisterRepeatVerbEvent(src, verbId));
    }

    public void StopRepeatingVerb(ClientObjectReference src, int verbId) {
        RaiseNetworkEvent(new UnregisterRepeatVerbEvent(src, verbId));
    }

    public IEnumerable<VerbInfo> GetAllVerbs() {
        return _verbs.Values;
    }

    /// <summary>
    ///     Find all the verbs the client is currently capable of executing
    /// </summary>
    /// <param name="ignoreHiddenAttr">Whether to ignore "set hidden = TRUE"</param>
    /// <returns>The ID, src, and information of every executable verb</returns>
    public IEnumerable<(int Id, ClientObjectReference Src, VerbInfo VerbInfo)> GetExecutableVerbs(
        bool ignoreHiddenAttr = false) {
        sbyte? seeInvisibility = null;
        if (_playerManager.LocalEntity != null) {
            _sightQuery.TryGetComponent(_playerManager.LocalEntity.Value, out DreamMobSightComponent? mobSight);

            seeInvisibility = mobSight?.SeeInvisibility;
        }

        // First, the verbs attached to our client
        if (_clientVerbs != null)
            foreach (int verbId in _clientVerbs) {
                if (!_verbs.TryGetValue(verbId, out VerbInfo verb))
                    continue;
                if (verb.IsHidden(ignoreHiddenAttr, seeInvisibility ?? 0))
                    continue; // TODO: How do invisible client verbs work when you don't have a mob?

                yield return (verbId, ClientObjectReference.Client, verb);
            }

        // Then, the verbs on objects around us
        var viewOverlay = _overlayManager.GetOverlay<DreamViewOverlay>();
        foreach (EntityUid entity in viewOverlay.EntitiesInView) {
            if (!_spriteQuery.TryGetComponent(entity, out DMISpriteComponent? sprite))
                continue;
            if (sprite.Icon.Appearance is not { } appearance)
                continue;

            foreach (int verbId in appearance.Verbs) {
                if (!_verbs.TryGetValue(verbId, out VerbInfo verb))
                    continue;
                if (verb.IsHidden(ignoreHiddenAttr, seeInvisibility!.Value))
                    continue;

                // Check the verb's "set src" allows us to execute this
                switch (verb.Accessibility) {
                    case VerbAccessibility.Usr: {
                        if (entity != _playerManager.LocalEntity)
                            continue;

                        break;
                    }
                    case VerbAccessibility.InUsr: {
                        if (_transformSystem.GetParentUid(entity) != _playerManager.LocalEntity)
                            continue;

                        break;
                    }
                    case VerbAccessibility.UsrLoc: {
                        if (_playerManager.LocalEntity is null)
                            continue;
                        if (_transformSystem.GetParentUid(_playerManager.LocalEntity.Value) != entity)
                            continue;

                        break;
                    }
                    case VerbAccessibility.UsrGroup: {
                        // TODO implement mob.group
                        break;
                    }
                    case VerbAccessibility.InWorld: {
                        break;
                    }
                    case VerbAccessibility.View:
                    case VerbAccessibility.InView:
                    case VerbAccessibility.OView:
                    case VerbAccessibility.InOView:
                    case VerbAccessibility.Range:
                    case VerbAccessibility.InRange:
                    case VerbAccessibility.ORange:
                    case VerbAccessibility.InORange: {
                        if (_playerManager.LocalEntity is not null)
                            continue;
                        if (verb.Range < 0)
                            continue;

                        EntityUid? usrUid = _playerManager.LocalEntity;
                        EntityUid srcUid = entity;

                        if (!_xformQuery.TryGetComponent(usrUid, out TransformComponent? usrXform) ||
                            !_xformQuery.TryGetComponent(srcUid, out TransformComponent? srcXform))
                            continue;

                        if (usrXform.GridUid != srcXform.GridUid)
                            continue;
                        if (usrXform.ParentUid == srcXform.ParentUid) {
                            Vector2 usrPos = _transformSystem.GetWorldPosition(usrXform);
                            Vector2 srcPos = _transformSystem.GetWorldPosition(srcXform);

                            if (usrPos != srcPos && (Math.Abs(usrPos.X - srcPos.X) > verb.Range ||
                                                     Math.Abs(usrPos.Y - srcPos.Y) > verb.Range))
                                continue;
                        } else {
                            if (srcXform.ParentUid == usrUid) {
                                if (verb.Accessibility.IsO())
                                    continue;
                            } else if (usrXform.ParentUid != srcUid) {
                                continue;
                            }
                        }

                        if (verb.Accessibility.IsView()) {
                            // TODO
                        }

                        break;
                    }
                }

                var src = new ClientObjectReference(_entityManager.GetNetEntity(entity));
                yield return (verbId, src, verb);
            }
        }

        // TODO: Turfs, Areas
    }

    /// <summary>
    ///     Find all the verbs the client is currently capable of executing on the given target
    /// </summary>
    /// <param name="target">The target of the verb</param>
    /// <returns>The ID, src, and information of every executable verb</returns>
    public IEnumerable<(int Id, ClientObjectReference Src, VerbInfo VerbInfo)> GetExecutableVerbs(
        ClientObjectReference target) {
        foreach ((int Id, ClientObjectReference Src, VerbInfo VerbInfo) verb in GetExecutableVerbs()) {
            DreamValueType? targetType = verb.VerbInfo.GetTargetType();
            switch (targetType)
            {
                case null:
                {
                    // Verbs without a target but an "in view()/range()" accessibility will still show
                    if (verb.Src.Equals(target) &&
                        verb.VerbInfo.Accessibility
                            is VerbAccessibility.InRange or VerbAccessibility.InORange
                            or VerbAccessibility.InView or VerbAccessibility.InOView)
                        yield return verb;

                    continue;
                }
                case DreamValueType.Anything:
                    yield return verb;
                    break;
                default:
                    switch (target.Type) {
                        case ClientObjectReference.RefType.Entity:
                            EntityUid entity = _entityManager.GetEntity(target.Entity);
                            bool isMob = _entityManager.HasComponent<DreamMobSightComponent>(entity);

                            if ((targetType & DreamValueType.Mob) != 0x0 && isMob)
                                yield return verb;
                            if ((targetType & DreamValueType.Obj) != 0x0 && !isMob)
                                yield return verb;

                            break;
                        case ClientObjectReference.RefType.Turf:
                            if ((targetType & DreamValueType.Turf) != 0x0)
                                yield return verb;

                            break;
                    }

                    break;
            }
        }
    }

    /// <summary>
    ///     Look for a verb with the given command-name that the client can execute
    /// </summary>
    /// <param name="commandName">Command-name to look for</param>
    /// <returns>The ID, target, and verb information if a verb was found</returns>
    public (int Id, ClientObjectReference Src, VerbInfo VerbInfo)? FindVerbWithCommandName(string commandName) {
        foreach ((int Id, ClientObjectReference Src, VerbInfo VerbInfo) verb in GetExecutableVerbs(true))
            if (verb.VerbInfo.GetCommandName() == commandName)
                return verb;

        return null;
    }

    /// <summary>
    ///     Open prompt windows for the user to enter the arguments to a verb
    /// </summary>
    /// <param name="verbInfo">The verb to get arguments for</param>
    /// <returns>The values the user gives</returns>
    private async Task<object?[]> PromptVerbArguments(VerbInfo verbInfo) {
        int argumentCount = verbInfo.Arguments.Length;
        object?[] arguments = argumentCount > 0 ? new object?[argumentCount] : Array.Empty<object?>();

        for (var i = 0; i < argumentCount; i++) {
            VerbArg arg = verbInfo.Arguments[i];
            var tcs = new TaskCompletionSource<object?>();

            _taskManager.RunOnMainThread(() => {
                _interfaceManager.Prompt(arg.Types, verbInfo.Name, arg.Name, string.Empty,
                    (_, value) => { tcs.SetResult(value); });
            });

            arguments[i] = await tcs.Task; // Wait for this prompt to finish before moving on to the next
        }

        return arguments;
    }

    private void OnAllVerbsEvent(AllVerbsEvent e) {
        _verbs.EnsureCapacity(e.Verbs.Count);

        for (var i = 0; i < e.Verbs.Count; i++) {
            VerbInfo verb = e.Verbs[i];

            _verbs.Add(i, verb);
        }

        _interfaceManager.DefaultInfo?.RefreshVerbs(this);
    }

    private void OnRegisterVerbEvent(RegisterVerbEvent e) {
        _verbs.Add(e.VerbId, e.VerbInfo);
    }

    private void OnUpdateClientVerbsEvent(UpdateClientVerbsEvent e) {
        _clientVerbs = e.VerbIds;
        _interfaceManager.DefaultInfo?.RefreshVerbs(this);
    }

    public void RefreshVerbs() {
        _interfaceManager.DefaultInfo?.RefreshVerbs(this);
    }
}
