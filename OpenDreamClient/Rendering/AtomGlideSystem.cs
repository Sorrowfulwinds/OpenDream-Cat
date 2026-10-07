using OpenDreamClient.Interface;
using Robust.Client.GameObjects;
using Robust.Shared.Utility;

namespace OpenDreamClient.Rendering;

/// <summary>
///     Disables RobustToolbox's transform lerping and replaces it with our own gliding
/// </summary>
public sealed partial class AtomGlideSystem : EntitySystem {
    private readonly List<Glide> _currentGlides = new();
    [Dependency] private IEntityManager _entityManager = default!;

    /// <summary>
    ///     Ignore MoveEvent when this is true.
    ///     Prevents an infinite loop when setting the position within the event handler.
    /// </summary>
    private bool _ignoreMoveEvent;

    [Dependency] private IDreamInterfaceManager _interfaceManager = default!;
    private EntityQuery<DMISpriteComponent> _spriteQuery;

    [Dependency] private TransformSystem _transformSystem = default!;

    public override void Initialize() {
        UpdatesBefore.Add(typeof(SharedTransformSystem));

        _spriteQuery = _entityManager.GetEntityQuery<DMISpriteComponent>();

        _transformSystem.OnGlobalMoveEvent += OnTransformMove;
    }

    public override void Shutdown() {
        _currentGlides.Clear();
    }

    public override void FrameUpdate(float frameTime) {
        // As of writing, Reset() does nothing but clear the transform system's _lerpingTransforms list
        // We update before SharedTransformSystem so this serves to disable RT's lerping, which fights our gliding
        // TODO: This kinda fights RT. Would be nice to modify RT to make it play nicer.
        _transformSystem.Reset();

        _ignoreMoveEvent = false;

        for (var i = 0; i < _currentGlides.Count; i++) {
            Glide glide = _currentGlides[i];

            if (_entityManager.Deleted(glide.Uid) || glide.Sprite.Icon.Appearance == null) {
                _currentGlides.RemoveSwap(i--);
                continue;
            }

            Vector2 currentPos = glide.Transform.LocalPosition;
            Vector2 newPos = currentPos;
            float movementSpeed =
                CalculateMovementSpeed(_interfaceManager.IconSize, glide.Sprite.Icon.Appearance.GlideSize);
            float movement = movementSpeed * frameTime;

            // Move X towards the end position at a constant speed
            if (!MathHelper.CloseTo(currentPos.X, glide.EndPos.X)) {
                if (currentPos.X < glide.EndPos.X)
                    newPos.X = Math.Min(glide.EndPos.X, newPos.X + movement);
                else if (currentPos.X > glide.EndPos.X)
                    newPos.X = Math.Max(glide.EndPos.X, newPos.X - movement);
            }

            // Move Y towards the end position at a constant speed
            if (!MathHelper.CloseTo(currentPos.Y, glide.EndPos.Y)) {
                if (currentPos.Y < glide.EndPos.Y)
                    newPos.Y = Math.Min(glide.EndPos.Y, newPos.Y + movement);
                else if (currentPos.Y > glide.EndPos.Y)
                    newPos.Y = Math.Max(glide.EndPos.Y, newPos.Y - movement);
            }

            if (newPos.EqualsApprox(glide.EndPos)) { // Glide is finished
                newPos = glide.EndPos;

                _currentGlides.RemoveSwap(i--);
            }

            _ignoreMoveEvent = true;
            _transformSystem.SetLocalPositionNoLerp(glide.Uid, newPos, glide.Transform);
            _ignoreMoveEvent = false;
        }
    }

    /// <summary>
    ///     Disables RT lerping and sets up the entity's glide
    /// </summary>
    private void OnTransformMove(ref MoveEvent e) {
        if (_ignoreMoveEvent || e.ParentChanged)
            return;
        if (!_spriteQuery.TryGetComponent(e.Sender, out DMISpriteComponent? sprite) || sprite.Icon?.Appearance is null)
            return;

        _ignoreMoveEvent = true;

        // Look for any in-progress glides on this transform
        Glide? glide = null;
        foreach (Glide potentiallyThisTransform in _currentGlides) {
            if (potentiallyThisTransform.Transform != e.Component)
                continue;

            glide = potentiallyThisTransform;
            break;
        }

        Vector2 startingFrom = glide?.EndPos ?? e.OldPosition.Position;
        Vector2 glidingTo = e.NewPosition.Position;

        // Moving a greater distance than 2 tiles. Don't glide.
        // TODO: Support step_size values (I think that's what decides whether or not to glide?)
        if ((glidingTo - startingFrom).Length() > 2f) {
            // Stop the current glide if there is one
            if (glide != null)
                _currentGlides.Remove(glide);

            _ignoreMoveEvent = false;
            return;
        }

        if (glide == null) {
            glide = new Glide(e.Sender, e.Component, sprite);
            _currentGlides.Add(glide);
        }

        // Move the transform to our starting point
        _transformSystem.SetLocalPositionNoLerp(e.Sender, startingFrom, e.Component);

        glide.EndPos = glidingTo;
        _ignoreMoveEvent = false;
    }

    private static float CalculateMovementSpeed(int iconSize, float glideSize) {
        if (glideSize == 0)
            glideSize = 4; // TODO: 0 gives us "automated control" over this value, not just setting it to 4

        // Assume a 20 TPS server
        // TODO: Support other TPS
        return glideSize / iconSize * 20f;
    }

    private sealed class Glide(EntityUid uid, TransformComponent transform, DMISpriteComponent sprite) {
        public readonly DMISpriteComponent Sprite = sprite;
        public readonly TransformComponent Transform = transform;
        public readonly EntityUid Uid = uid;
        public Vector2 EndPos;
    }
}
