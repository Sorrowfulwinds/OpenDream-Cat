using OpenDreamClient.Input.ContextMenu;
using OpenDreamClient.Interface;
using OpenDreamClient.Interface.Controls.UI;
using OpenDreamClient.Rendering;
using OpenDreamShared.Dream;
using OpenDreamShared.Input;
using OpenDreamShared.Interface.Descriptors;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Client.Input;
using Robust.Client.UserInterface;
using Robust.Shared;
using Robust.Shared.Configuration;
using Robust.Shared.Input;
using Robust.Shared.Input.Binding;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;

namespace OpenDreamClient.Input;

internal sealed partial class MouseInputSystem : SharedMouseInputSystem {
    [Dependency] private ClientAppearanceSystem _appearanceSystem = default!;
    [Dependency] private IClyde _clyde = default!;
    [Dependency] private IConfigurationManager _configurationManager = default!;
    private ContextMenuPopup _contextMenu = default!;
    [Dependency] private IDreamInterfaceManager _dreamInterfaceManager = default!;

    private DreamViewOverlay? _dreamViewOverlay;
    [Dependency] private IEntityManager _entityManager = default!;
    [Dependency] private IInputManager _inputManager = default!;
    [Dependency] private ILogManager _logManager = default!;
    [Dependency] private SharedMapSystem _mapManager = default!;
    [Dependency] private MapSystem _mapSystem = default!;
    [Dependency] private IOverlayManager _overlayManager = default!;
    private ISawmill _sawmill = default!;
    private EntityClickInformation? _selectedEntity;
    [Dependency] private IUserInterfaceManager _userInterfaceManager = default!;

    public override void Initialize() {
        UpdatesOutsidePrediction = true;
        _sawmill = _logManager.GetSawmill("opendream.mouseinput");

        _contextMenu = new ContextMenuPopup();
        _userInterfaceManager.ModalRoot.AddChild(_contextMenu);
    }

    public override void Update(float frameTime) {
        if (_selectedEntity == null)
            return;

        if (!_selectedEntity.IsDrag) {
            Vector2 currentMousePos = _inputManager.MouseScreenPosition.Position;
            float distance = (currentMousePos - _selectedEntity.InitialMousePos.Position).Length();

            if (distance > 3f) {
                _selectedEntity.IsDrag = true;
                if (_dreamInterfaceManager.DefaultMap is { } map)
                    UpdateMouseCursor(map.Viewport, _selectedEntity.Atom);
            }
        }
    }

    public override void Shutdown() {
        CommandBinds.Unregister<MouseInputSystem>();
    }

    public bool HandleViewportEvent(ScalingViewport viewport, GUIBoundKeyEventArgs args, ControlDescriptor descriptor) {
        if (args.State == BoundKeyState.Down)
            return OnPress(viewport, args, descriptor);
        return OnRelease(viewport, args);
    }

    public void HandleStatClick(string atomRef, bool isRight, bool isMiddle) {
        bool shift = _inputManager.IsKeyDown(Keyboard.Key.Shift);
        bool ctrl = _inputManager.IsKeyDown(Keyboard.Key.Control);
        bool alt = _inputManager.IsKeyDown(Keyboard.Key.Alt);

        RaiseNetworkEvent(new StatClickedEvent(atomRef, isRight, isMiddle, shift, ctrl, alt));
    }

    public void HandleAtomMouseEntered(ScalingViewport viewport, Vector2 relativePos, ClientObjectReference atomRef,
        Vector2i iconPos) {
        UpdateMouseCursor(viewport, atomRef);
        if (!HasMouseEventEnabled(atomRef, AtomMouseEvents.Enter))
            return;

        RaiseNetworkEvent(new MouseEnteredEvent(atomRef, CreateClickParams(viewport, relativePos, iconPos)));
    }

    public void HandleAtomMouseExited(ScalingViewport viewport, ClientObjectReference atomRef) {
        UpdateMouseCursor(viewport, null);
        if (!HasMouseEventEnabled(atomRef, AtomMouseEvents.Exit))
            return;

        RaiseNetworkEvent(new MouseExitedEvent(atomRef, CreateClickParams(viewport, Vector2.Zero, Vector2i.Zero)));
    }

    public void HandleAtomMouseMove(ScalingViewport viewport, Vector2 relativePos, ClientObjectReference atomRef,
        Vector2i iconPos) {
        if (!HasMouseEventEnabled(atomRef, AtomMouseEvents.Move))
            return;

        RaiseNetworkEvent(new MouseMoveEvent(atomRef, CreateClickParams(viewport, relativePos, iconPos)));
    }

    public (ClientObjectReference Atom, Vector2i IconPosition, bool IsScreen)? GetAtomUnderMouse(
        ScalingViewport viewport, Vector2 relativePos, ScreenCoordinates globalPos) {
        _dreamViewOverlay ??= _overlayManager.GetOverlay<DreamViewOverlay>();
        if (_dreamViewOverlay.MouseMap == null)
            return null;

        UIBox2i viewportBox = viewport.GetDrawBox();
        if (!viewportBox.Contains((int)relativePos.X, (int)relativePos.Y))
            return null; // Was outside of the viewport

        MapCoordinates mapCoords = viewport.ScreenToMap(globalPos.Position);
        Vector2 mousePos = (relativePos - viewportBox.TopLeft) / viewportBox.Size * viewport.ViewportSize;
        if (mousePos.X >= _dreamViewOverlay.MouseMap.Size.X || mousePos.Y >= _dreamViewOverlay.MouseMap.Size.Y)
            return null;

        if (_configurationManager.GetCVar(CVars.DisplayCompat))
            return null; //Compat mode causes crashes with RT's GetPixel because OpenGL ES doesn't support GetTexImage()
        Color lookupColor = _dreamViewOverlay.MouseMap.GetPixel((int)mousePos.X, (int)mousePos.Y);
        RendererMetaData? underMouse = _dreamViewOverlay.MouseMapLookup.GetValueOrDefault(lookupColor);
        if (underMouse == null)
            return null;

        if (underMouse.ClickUid == EntityUid.Invalid) { // A turf
            (ClientObjectReference Atom, Vector2i IconPosition)? turf = GetTurfUnderMouse(mapCoords, out _);
            if (turf == null)
                return null;

            return (turf.Value.Atom, turf.Value.IconPosition, false);
        }

        var iconPosition = (Vector2i)((mapCoords.Position - underMouse.Position) * _dreamInterfaceManager.IconSize);
        var reference = new ClientObjectReference(_entityManager.GetNetEntity(underMouse.ClickUid));

        return (reference, iconPosition, underMouse.IsScreen);
    }

    public (ClientObjectReference Atom, Vector2i IconPosition)? GetTurfUnderMouse(MapCoordinates mapCoords,
        out uint? turfId) {
        // Grid coordinates are half a meter off from entity coordinates
        mapCoords = new MapCoordinates(mapCoords.Position + new Vector2(0.5f), mapCoords.MapId);

        if (_mapManager.TryFindGridAt(mapCoords, out EntityUid gridEntity, out MapGridComponent? grid)) {
            Vector2i position =
                _mapSystem.CoordinatesToTile(gridEntity, grid, _mapSystem.MapToGrid(gridEntity, mapCoords));
            _mapSystem.TryGetTile(grid, position, out Tile tile);
            turfId = (uint)tile.TypeId;
            var turfIconPosition = (Vector2i)((mapCoords.Position - position) * _dreamInterfaceManager.IconSize);
            MapCoordinates worldPosition = _mapSystem.GridTileToWorld(gridEntity, grid, position);

            return (new ClientObjectReference(position, (int)worldPosition.MapId), turfIconPosition);
        }

        turfId = null;
        return null;
    }

    private bool OnPress(ScalingViewport viewport, GUIBoundKeyEventArgs args, ControlDescriptor descriptor) {
        //either turf or atom was clicked, and it was a right-click, and the popup menu is enabled, and the right-click parameter is disabled
        if (args.Function == EngineKeyFunctions.UIRightClick && _dreamInterfaceManager.ShowPopupMenus &&
            !descriptor.RightClick.Value) {
            _contextMenu.RepopulateEntities(viewport, args.RelativePosition, args.PointerLocation);
            if (_contextMenu.EntityCount != 0) { //don't open a 1x1 empty context menu
                Vector2 contextMenuLocation =
                    args.PointerLocation.Position /
                    _userInterfaceManager.ModalRoot.UIScale; // Take scaling into account

                _contextMenu.Measure(_userInterfaceManager.ModalRoot.Size);
                _contextMenu.Open(UIBox2.FromDimensions(contextMenuLocation, _contextMenu.DesiredSize));
            }

            return true;
        }

        (ClientObjectReference Atom, Vector2i IconPosition, bool IsScreen)? underMouse =
            GetAtomUnderMouse(viewport, args.RelativePixelPosition, args.PointerLocation);
        if (underMouse == null)
            return false;

        ClientObjectReference atom = underMouse.Value.Atom;
        ClickParams
            clickParams =
                CreateClickParams(viewport, args,
                    underMouse.Value
                        .IconPosition); // If client.show_popup_menu is disabled, this will handle sending right clicks

        _selectedEntity = new EntityClickInformation(atom, args.PointerLocation, clickParams);
        return true;
    }

    private bool OnRelease(ScalingViewport viewport, GUIBoundKeyEventArgs args) {
        if (_selectedEntity == null) {
            UpdateMouseCursor(viewport, null);
            return false;
        }

        (ClientObjectReference Atom, Vector2i IconPosition, bool IsScreen)? overAtom =
            GetAtomUnderMouse(viewport, args.RelativePixelPosition, args.PointerLocation);
        if (!_selectedEntity.IsDrag)
            RaiseNetworkEvent(new AtomClickedEvent(_selectedEntity.Atom, _selectedEntity.ClickParams));
        else
            RaiseNetworkEvent(new AtomDraggedEvent(_selectedEntity.Atom, overAtom?.Atom, _selectedEntity.ClickParams));

        _selectedEntity = null;
        UpdateMouseCursor(viewport, overAtom?.Atom);
        return true;
    }

    private void UpdateMouseCursor(ScalingViewport viewport, ClientObjectReference? mouseOver) {
        bool isDragging = _selectedEntity?.IsDrag ?? false;
        if (!mouseOver.HasValue ||
            !_appearanceSystem.TryGetAppearance(mouseOver.Value, out ImmutableAppearance? mouseOverAppearance)) {
            if (!isDragging)
                SetCursorFromDefine(1, _dreamInterfaceManager.Cursors.BaseCursor, viewport);
            return;
        }

        if (isDragging) {
            if (!_appearanceSystem.TryGetAppearance(_selectedEntity!.Atom,
                    out ImmutableAppearance? draggingAppearance)) {
                SetCursorFromDefine(1, _dreamInterfaceManager.Cursors.DragCursor, viewport);
                return;
            }

            int define = mouseOverAppearance.MouseDropZone
                ? mouseOverAppearance.MouseDropPointer
                : draggingAppearance.MouseDragPointer;
            ICursor? cursor = mouseOverAppearance.MouseDropZone
                ? _dreamInterfaceManager.Cursors.DropCursor
                : _dreamInterfaceManager.Cursors.DragCursor;
            SetCursorFromDefine(define, cursor, viewport);
        } else {
            SetCursorFromDefine(mouseOverAppearance.MouseOverPointer, _dreamInterfaceManager.Cursors.OverCursor,
                viewport);
        }
    }

    private void SetCursorFromDefine(int define, ICursor? activeCursor, ScalingViewport viewport) {
        _sawmill.Verbose($"SetCursor {define} {activeCursor}");

        if (_dreamInterfaceManager.Cursors.AllStateSet)
            viewport.CustomCursorShape = _dreamInterfaceManager.Cursors.BaseCursor;
        else
            viewport.CustomCursorShape = define switch {
                0 => _dreamInterfaceManager.Cursors.BaseCursor, //MOUSE_INACTIVE_POINTER
                1 => activeCursor, //MOUSE_ACTIVE_POINTER
                //skipping 2 is intentional, it's what byond does
                3 => _clyde.GetStandardCursor(StandardCursorShape.Crosshair), //MOUSE_DRAG_POINTER
                4 => _clyde.GetStandardCursor(StandardCursorShape.Hand), //MOUSE_DROP_POINTER
                5 => _clyde.GetStandardCursor(StandardCursorShape.Arrow), //MOUSE_ARROW_POINTER
                6 => _clyde.GetStandardCursor(StandardCursorShape.Crosshair), //MOUSE_CROSSHAIRS_POINTER
                7 => _clyde.GetStandardCursor(StandardCursorShape.Hand), //MOUSE_HAND_POINTER
                _ => null
            };

        _clyde.SetCursor(viewport.CustomCursorShape);
    }

    private ClickParams CreateClickParams(ScalingViewport viewport, GUIBoundKeyEventArgs args, Vector2i iconPos) {
        bool right = args.Function == EngineKeyFunctions.UIRightClick;
        bool middle = args.Function == OpenDreamKeyFunctions.MouseMiddle;
        bool shift = _inputManager.IsKeyDown(Keyboard.Key.Shift);
        bool ctrl = _inputManager.IsKeyDown(Keyboard.Key.Control);
        bool alt = _inputManager.IsKeyDown(Keyboard.Key.Alt);
        UIBox2i viewportBox = viewport.GetDrawBox();
        Vector2 screenLocPos = (args.RelativePixelPosition - viewportBox.TopLeft) / viewportBox.Size *
                               viewport.ViewportSize;
        float screenLocY = viewport.ViewportSize.Y - screenLocPos.Y; // Flip the Y
        var screenLoc = new ScreenLocation((int)screenLocPos.X, (int)screenLocY, 32); // TODO: icon_size other than 32

        // TODO: Take icon transformations into account for iconPos
        return new ClickParams(screenLoc, right, middle, shift, ctrl, alt, iconPos.X, iconPos.Y);
    }

    /// <summary>
    ///     <see
    ///         cref="CreateClickParams(OpenDreamClient.Interface.Controls.UI.ScalingViewport,Robust.Client.UserInterface.GUIBoundKeyEventArgs,Robust.Shared.Maths.Vector2i)" />
    ///     but without information about mouse/keyboard buttons
    /// </summary>
    private ClickParams CreateClickParams(ScalingViewport viewport, Vector2 relativePos, Vector2i iconPos) {
        UIBox2i viewportBox = viewport.GetDrawBox();
        Vector2 screenLocPos = (relativePos - viewportBox.TopLeft) / viewportBox.Size * viewport.ViewportSize;
        float screenLocY = viewport.ViewportSize.Y - screenLocPos.Y; // Flip the Y
        var screenLoc = new ScreenLocation((int)screenLocPos.X, (int)screenLocY, 32); // TODO: icon_size other than 32

        // TODO: Take icon transformations into account for iconPos
        return new ClickParams(screenLoc, false, false, false, false, false, iconPos.X, iconPos.Y);
    }

    private bool HasMouseEventEnabled(ClientObjectReference atomRef, AtomMouseEvents mouseEvent) {
        if (!_appearanceSystem.TryGetAppearance(atomRef, out ImmutableAppearance? appearance))
            return false;

        return appearance.EnabledMouseEvents.HasFlag(mouseEvent);
    }

    private sealed class EntityClickInformation(
        ClientObjectReference atom,
        ScreenCoordinates initialMousePos,
        ClickParams clickParams) {
        public readonly ClientObjectReference Atom = atom;
        public readonly ClickParams ClickParams = clickParams;
        public readonly ScreenCoordinates InitialMousePos = initialMousePos;
        public bool IsDrag; // If the current click is considered a drag (if the mouse has moved after the click)
    }
}
