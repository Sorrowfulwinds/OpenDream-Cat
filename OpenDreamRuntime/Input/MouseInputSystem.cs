using System.Text;
using OpenDreamRuntime.Map;
using OpenDreamRuntime.Objects;
using OpenDreamRuntime.Objects.Types;
using OpenDreamShared.Dream;
using OpenDreamShared.Input;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace OpenDreamRuntime.Input;

internal sealed partial class MouseInputSystem : SharedMouseInputSystem {
    private readonly TimeSpan _doubleClickDelay = TimeSpan.FromMilliseconds(250);
    [Dependency] private AtomManager _atomManager = default!;
    [Dependency] private DreamManager _dreamManager = default!;
    [Dependency] private IDreamMapManager _mapManager = default!;
    [Dependency] private DreamRefManager _refManager = default!;
    [Dependency] private IGameTiming _timing = default!;

    public override void Initialize() {
        base.Initialize();

        SubscribeNetworkEvent<AtomClickedEvent>(OnAtomClicked);
        SubscribeNetworkEvent<AtomDraggedEvent>(OnAtomDragged);
        SubscribeNetworkEvent<StatClickedEvent>(OnStatClicked);
        SubscribeNetworkEvent<MouseEnteredEvent>(OnMouseEntered);
        SubscribeNetworkEvent<MouseExitedEvent>(OnMouseExited);
        SubscribeNetworkEvent<MouseMoveEvent>(OnMouseMove);
    }

    private void OnAtomClicked(AtomClickedEvent e, EntitySessionEventArgs sessionEvent) {
        DreamConnection connection = _dreamManager.GetConnectionBySession(sessionEvent.SenderSession);
        DreamObject? clicked = _dreamManager.GetFromClientReference(connection, e.ClickedAtom);
        if (clicked is not DreamObjectAtom atom)
            return;

        HandleAtomClick(e, atom, sessionEvent);
    }

    private void OnAtomDragged(AtomDraggedEvent e, EntitySessionEventArgs sessionEvent) {
        DreamConnection connection = _dreamManager.GetConnectionBySession(sessionEvent.SenderSession);
        DreamObject? src = _dreamManager.GetFromClientReference(connection, e.SrcAtom);
        if (src is not DreamObjectAtom srcAtom)
            return;

        DreamObjectMob? usr = connection.Mob;
        (int X, int Y, int Z) srcPos = _atomManager.GetAtomPosition(srcAtom);
        DreamObjectAtom? over = e.OverAtom != null
            ? _dreamManager.GetFromClientReference(connection, e.OverAtom.Value) as DreamObjectAtom
            : null;

        _mapManager.TryGetTurfAt((srcPos.X, srcPos.Y), srcPos.Z, out DreamObjectTurf? srcLoc);

        DreamValue overLocValue = DreamValue.Null;
        if (over != null) {
            (int X, int Y, int Z) overPos = _atomManager.GetAtomPosition(over);

            _mapManager.TryGetTurfAt((overPos.X, overPos.Y), overPos.Z, out DreamObjectTurf? overLoc);
            overLocValue = new DreamValue(overLoc);
        }

        connection.Client?.SpawnProc("MouseDrop", usr,
            new DreamValue(src),
            new DreamValue(over),
            new DreamValue(srcLoc), // TODO: Location can be a skin element
            overLocValue,
            DreamValue.Null, // TODO: src_control and over_control
            DreamValue.Null,
            new DreamValue(ConstructClickParams(e.Params))).Dispose();
    }

    private void OnStatClicked(StatClickedEvent e, EntitySessionEventArgs sessionEvent) {
        using DreamValue atom = _refManager.LocateRef(e.AtomRef);
        if (!atom.TryGetValueAsDreamObject<DreamObjectAtom>(out DreamObjectAtom? dreamObject))
            return;

        HandleAtomClick(e, dreamObject, sessionEvent);
    }

    private void OnMouseEntered(MouseEnteredEvent e, EntitySessionEventArgs sessionEvent) {
        DreamConnection connection = _dreamManager.GetConnectionBySession(sessionEvent.SenderSession);
        DreamObject? atom = _dreamManager.GetFromClientReference(connection, e.Atom);
        if (atom is not DreamObjectAtom)
            return;
        if (!_atomManager.GetEnabledMouseEvents(atom).HasFlag(AtomMouseEvents.Enter))
            return;

        using DreamValue loc = atom.GetVariable("loc");
        atom.SpawnProc("MouseEntered", connection.Mob,
            loc,
            DreamValue.Null,
            new DreamValue(ConstructClickParams(e.Params))).Dispose();
    }

    private void OnMouseExited(MouseExitedEvent e, EntitySessionEventArgs sessionEvent) {
        DreamConnection connection = _dreamManager.GetConnectionBySession(sessionEvent.SenderSession);
        DreamObject? atom = _dreamManager.GetFromClientReference(connection, e.Atom);
        if (atom is not DreamObjectAtom)
            return;
        if (!_atomManager.GetEnabledMouseEvents(atom).HasFlag(AtomMouseEvents.Exit))
            return;

        using DreamValue loc = atom.GetVariable("loc");
        atom.SpawnProc("MouseExited", connection.Mob,
            loc,
            DreamValue.Null,
            new DreamValue(ConstructClickParams(e.Params))).Dispose();
    }

    private void OnMouseMove(MouseMoveEvent e, EntitySessionEventArgs sessionEvent) {
        DreamConnection connection = _dreamManager.GetConnectionBySession(sessionEvent.SenderSession);
        DreamObject? atom = _dreamManager.GetFromClientReference(connection, e.Atom);
        if (atom is not DreamObjectAtom)
            return;
        if (!_atomManager.GetEnabledMouseEvents(atom).HasFlag(AtomMouseEvents.Move))
            return;

        using DreamValue loc = atom.GetVariable("loc");
        atom.SpawnProc("MouseMove", connection.Mob,
            loc,
            DreamValue.Null,
            new DreamValue(ConstructClickParams(e.Params))).Dispose();
    }

    private void HandleAtomClick(IAtomMouseEvent e, DreamObjectAtom atom, EntitySessionEventArgs sessionEvent) {
        ICommonSession session = sessionEvent.SenderSession;
        DreamConnection connection = _dreamManager.GetConnectionBySession(session);
        DreamObjectMob? usr = connection.Mob;

        string clickParams = ConstructClickParams(e.Params);

        // Double click fires before the second Click() fires
        if (_timing.RealTime - connection.LastClickTime <= _doubleClickDelay)
            connection.Client?.SpawnProc("DblClick", usr,
                new DreamValue(atom),
                DreamValue.Null,
                DreamValue.Null,
                new DreamValue(clickParams)).Dispose();

        connection.Client?.SpawnProc("Click", usr,
            new DreamValue(atom),
            DreamValue.Null,
            DreamValue.Null,
            new DreamValue(clickParams)).Dispose();

        connection.LastClickTime = _timing.RealTime;
    }

    private string ConstructClickParams(ClickParams clickParams) {
        var paramsBuilder =
            new StringBuilder(96); // Click param strings are typically ~86 chars with all modifiers held. 96 is 64*1.5

        // All of these parameters have been ordered with BYOND parity

        paramsBuilder.Append($"icon-x={clickParams.IconX.ToString()};");
        paramsBuilder.Append($"icon-y={clickParams.IconY.ToString()};");

        string button;

        // Handles setting left=1, right=1, or middle=1 mouse param
        if (clickParams.Right) {
            paramsBuilder.Append("right=1;");
            button = "right";
        } else if (clickParams.Middle) {
            paramsBuilder.Append("middle=1;");
            button = "middle";
        } else {
            paramsBuilder.Append("left=1;");
            button = "left";
        }

        // Modifier keys
        if (clickParams.Ctrl) paramsBuilder.Append("ctrl=1;");
        if (clickParams.Shift) paramsBuilder.Append("shift=1;");
        if (clickParams.Alt) paramsBuilder.Append("alt=1;");

        paramsBuilder.Append($"button={button};");

        // Screen loc
        paramsBuilder.Append($"screen-loc={clickParams.ScreenLoc.ToCoordinates()}");

        return paramsBuilder.ToString();
    }
}
