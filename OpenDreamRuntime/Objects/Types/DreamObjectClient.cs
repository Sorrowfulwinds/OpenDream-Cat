using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using OpenDreamRuntime.Procs.Native;
using OpenDreamRuntime.Rendering;
using OpenDreamRuntime.Resources;
using OpenDreamShared.Dream;

namespace OpenDreamRuntime.Objects.Types;

public sealed class DreamObjectClient : DreamObject {
    public readonly ClientVerbsList ClientVerbs;
    public readonly DreamConnection Connection;
    public readonly ClientImagesList Images;
    public readonly ClientScreenList Screen;
    public IconResource? CursorIcon;

    public DreamObjectClient(DreamObjectDefinition objectDefinition, DreamConnection connection,
        ServerScreenOverlaySystem? screenOverlaySystem,
        ServerClientImagesSystem? clientImagesSystem) : base(objectDefinition) {
        Connection = connection;
        Screen = new ClientScreenList(ObjectTree, screenOverlaySystem, Connection);
        ClientVerbs = new ClientVerbsList(ObjectTree, this);
        Images = new ClientImagesList(ObjectTree, clientImagesSystem, Connection);

        DreamManager.Clients.Add(this);

        View = DreamManager.WorldInstance.DefaultView;
    }

    public ViewRange View { get; private set; }
    public bool ShowPopupMenus { get; private set; } = true;

    protected override void HandleDeletion() {
        Connection.Session?.Channel.Disconnect("Your client object was deleted");
        DreamManager.Clients.Remove(this);

        Screen.DecRef();
        ClientVerbs.DecRef();
        Images.DecRef();

        base.HandleDeletion();
    }

    protected override bool TryGetVar(string varName, out DreamValue value) {
        switch (varName) {
            case "ckey":
                value = new DreamValue(DreamProcNativeHelpers.Ckey(Connection.Key));
                return true;
            case "key":
                value = new DreamValue(Connection.Key);
                return true;
            case "mob":
                Connection.Mob?.IncRef();
                value = new DreamValue(Connection.Mob);
                return true;
            case "statobj":
                Connection.StatObj.IncRef();
                value = Connection.StatObj;
                return true;
            case "eye":
                Connection.Eye?.IncRef();
                value = new DreamValue(Connection.Eye);
                return true;
            case "view":
                // Number if square & centerable, string representation otherwise
                if (View.CanSquareRange)
                    value = new DreamValue(View.SquareRange.Value);
                else
                    value = new DreamValue(View.ToString());

                return true;
            case "computer_id"
                : // FIXME: This is not secure! Whenever RT implements a more robust (heh) method of uniquely identifying computers, replace this impl with that.
                var md5 = MD5.Create();
                // Check on Robust.Shared.Network.NetUserData.HWId" if you want to seed from how RT does user identification.
                // We don't use it here because it is probably not enough to ensure security, and (as of time of writing) only works on Windows machines.
                byte[] brown = Encoding.UTF8.GetBytes(Connection.Key);
                byte[] hash = md5.ComputeHash(brown);
                string hashStr =
                    BitConverter.ToString(hash).Replace("-", "").ToLower()
                        .Substring(0, 15); // Extracting the first 15 digits to ensure it'll fit in a 64-bit number

                value = new DreamValue(long.Parse(hashStr, NumberStyles.HexNumber)
                    .ToString()); // Converts from hex to decimal. Output is in analogous format to BYOND's.
                return true;
            case "address":
                // TODO: Session could be null if this is gotten from /mob/Logout() or /client/Del()
                // BYOND's behavior there needs tested
                value = new DreamValue(Connection.Session!.Channel.RemoteEndPoint.Address.ToString());
                return true;
            case "inactivity":
                value = new DreamValue(0); // TODO
                return true;
            case "timezone":
                value = new DreamValue(0); // TODO
                return true;
            case "statpanel":
                value = Connection.SelectedStatPanel is null
                    ? DreamValue.Null
                    : new DreamValue(Connection.SelectedStatPanel);
                return true;
            case "connection":
                value = new DreamValue("seeker");
                return true;
            case "screen":
                Screen.IncRef();
                value = new DreamValue(Screen);
                return true;
            case "verbs":
                ClientVerbs.IncRef();
                value = new DreamValue(ClientVerbs);
                return true;
            case "show_popup_menus":
                value = new DreamValue(ShowPopupMenus ? 1 : 0);
                return true;
            case "images":
                Images.IncRef();
                value = new DreamValue(Images);
                return true;
            case "mouse_pointer_icon":
                value = CursorIcon is null ? DreamValue.Null : new DreamValue(CursorIcon);
                return true;
            default:
                return base.TryGetVar(varName, out value);
        }
    }

    protected override void SetVar(string varName, DreamValue value) {
        switch (varName) {
            case "mob": {
                value.TryGetValueAsDreamObject<DreamObjectMob>(out DreamObjectMob? newMob);

                Connection.Mob = newMob;
                break;
            }
            case "statobj":
                value.IncRef();
                Connection.StatObj.DecRef();
                Connection.StatObj = value;
                break;
            case "eye": {
                value.TryGetValueAsDreamObject<DreamObjectAtom>(out DreamObjectAtom? newEye);
                if (newEye is not (DreamObjectMovable or null))
                    throw new DMException($"Cannot set eye to non-movable {value}"); // TODO: You can set it to a turf

                newEye?.IncRef();
                Connection.Eye?.DecRef();
                Connection.Eye = newEye as DreamObjectMovable;
                break;
            }
            case "view": {
                if (value.TryGetValueAsInteger(out int viewInt))
                    View = new ViewRange(viewInt);
                else if (value.TryGetValueAsString(out string? viewStr))
                    View = new ViewRange(viewStr);
                else
                    View = DreamManager.WorldInstance.DefaultView;

                Connection.SendClientInfoUpdate();
                break;
            }
            case "show_popup_menus": {
                // TODO: See what BYOND does with non-integer values. Per the ref only 0 should disable, but this needs to be verified.
                if (value.TryGetValueAsInteger(out int viewInt) && viewInt == 0)
                    ShowPopupMenus = false;
                else
                    ShowPopupMenus = true;

                Connection.SendClientInfoUpdate();
                break;
            }
            case "screen": {
                Screen.Cut();

                if (value.TryGetValueAsDreamList(out DreamList? valueList))
                    foreach (DreamValue screenValue in valueList.EnumerateValues())
                        Screen.AddValue(screenValue);
                else if (!value.IsNull) Screen.AddValue(value);

                break;
            }
            case "images": {
                Images.Cut();

                if (value.TryGetValueAsDreamList(out DreamList? valueList))
                    foreach (DreamValue screenValue in valueList.EnumerateValues())
                        Images.AddValue(screenValue);
                else if (!value.IsNull) Images.AddValue(value);

                break;
            }
            case "verbs": {
                ClientVerbs.Cut();

                if (value.TryGetValueAsDreamList(out DreamList? valueList))
                    foreach (DreamValue verbValue in valueList.EnumerateValues())
                        ClientVerbs.AddValue(verbValue);
                else
                    ClientVerbs.AddValue(value);

                break;
            }
            case "statpanel":
                if (!value.TryGetValueAsString(out string? statPanel))
                    return;

                Connection.SelectedStatPanel = statPanel;
                break;
            case "mouse_pointer_icon":
                //resolve the value to an icon file
                if (value.TryGetValueAsDreamResource(out DreamResource? iconResource) &&
                    iconResource is IconResource resource)
                    CursorIcon = resource;
                else
                    CursorIcon = null;

                Connection.SendClientInfoUpdate();
                break;
            default:
                base.SetVar(varName, value);
                break;
        }
    }

    public override void OperatorOutput(DreamValue b) {
        Connection.OutputDreamValue(b);
    }
}
