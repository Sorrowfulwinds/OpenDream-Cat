using System.Diagnostics.CodeAnalysis;
using OpenDreamShared.Interface.Descriptors;
using OpenDreamShared.Interface.DMF;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;

namespace OpenDreamClient.Interface.Controls;

public sealed partial class ControlWindow : InterfaceControl {
    // NOTE: a "window" in BYOND does not necessarily map 1:1 to OS windows.
    // Just like in win32 (which is definitely what this is inspired by let's be real),
    // windows can be embedded into other windows as a way to do nesting.

    public readonly List<InterfaceControl> ChildControls = new();

    private readonly ISawmill _sawmill = Logger.GetSawmill("opendream.window");
    private LayoutContainer _canvas = default!;
    [Dependency] private IClyde _clyde = default!;

    private string _currentStatus = string.Empty;

    private PanelContainer _menuContainer = default!;

    private (OSWindow? osWindow, IClydeWindow? clydeWindow) _myWindow;
    [Dependency] private IUserInterfaceManager _uiMgr = default!;

    public ControlWindow(WindowDescriptor windowDescriptor) : base(windowDescriptor, null) {
        IoCManager.InjectDependencies(this);
    }

    public string Title => WindowDescriptor.Title.Value;
    public InterfaceMacroSet Macro => InterfaceManager.MacroSets[WindowDescriptor.Macro.AsRaw()];

    public WindowDescriptor WindowDescriptor => (WindowDescriptor)ElementDescriptor;

    public IClydeWindow? GetClydeWindow() {
        return _myWindow.osWindow is null ? _myWindow.clydeWindow : _myWindow.osWindow.ClydeWindow;
    }

    protected override void UpdateElementDescriptor() {
        // Don't call base.UpdateElementDescriptor();

        _menuContainer.RemoveAllChildren();
        if (InterfaceManager.Menus.TryGetValue(WindowDescriptor.Menu.Value, out InterfaceMenu? menu)) {
            _menuContainer.AddChild(menu.MenuBar);
            _menuContainer.Visible = true;
        } else {
            _menuContainer.Visible = false;
        }

        if (!WindowDescriptor.IsPane.Value)
            UpdateWindowAttributes(_myWindow);

        if (WindowDescriptor.IsDefault.Value) Macro.SetActive();
    }

    /// <summary>
    ///     Closes the window if it is a child window. No effect if it is either a default window or a pane
    /// </summary>
    public void CloseChildWindow() {
        if (_myWindow.osWindow is not null)
            _myWindow.osWindow.Close();
    }

    public OSWindow CreateWindow() {
        if (_myWindow.osWindow is not null)
            return _myWindow.osWindow;

        OSWindow window = new();
        if (UIElement.Parent is not null)
            UIElement.Orphan();
        window.Children.Add(UIElement);

        if (ControlDescriptor.Size.X == 0)
            window.SetWidth = window.MaxWidth;
        else
            window.SetWidth = ControlDescriptor.Size.X;
        if (ControlDescriptor.Size.Y == 0)
            window.SetHeight = window.MaxHeight;
        else
            window.SetHeight = ControlDescriptor.Size.Y;

        window.Closing += _ => {
            // A window can have a command set to be run when it's closed
            if (!string.IsNullOrWhiteSpace(WindowDescriptor.OnClose.Value))
                InterfaceManager.RunCommand(WindowDescriptor.OnClose.Value);

            _myWindow = (null, _myWindow.clydeWindow);
        };
        window.StartupLocation = WindowStartupLocation.CenterOwner;
        window.Owner = _clyde.MainWindow;

        _myWindow = (window, _myWindow.clydeWindow);
        window.Create();
        UpdateWindowAttributes(_myWindow);
        return window;
    }

    public void RegisterOnClydeWindow(IClydeWindow window) {
        // todo: listen for closed.
        if (_myWindow.osWindow is not null) {
            _myWindow.osWindow.Close();
            UIElement.Orphan();
        }

        _myWindow = (null, window);
        UpdateWindowAttributes(_myWindow);
    }

    public void UpdateAnchors() {
        DMFPropertySize windowSize = Size;
        if (windowSize.X == 0)
            windowSize.X = _canvas.PixelWidth;
        if (windowSize.Y == 0)
            windowSize.Y = _canvas.PixelHeight;

        for (var i = 0; i < ChildControls.Count; i++) {
            InterfaceControl control = ChildControls[i];
            Control element = control.UIElement;
            DMFPropertyPos elementPos = control.Pos;
            DMFPropertySize elementSize = control.Size;

            if (control.Anchor1.HasValue) {
                Vector2i anchorTo = control.AnchorPosition;

                // Defaults to anchoring relative to the DMF-defined size
                if (anchorTo.X == 0)
                    anchorTo.X = Size.X;
                if (anchorTo.Y == 0)
                    anchorTo.Y = Size.Y;

                float offset1X = elementPos.X - anchorTo.X * control.Anchor1.Value.X / 100f;
                float offset1Y = elementPos.Y - anchorTo.Y * control.Anchor1.Value.Y / 100f;
                float left = _canvas.Width * control.Anchor1.Value.X / 100 + offset1X;
                float top = _canvas.Height * control.Anchor1.Value.Y / 100 + offset1Y;
                LayoutContainer.SetMarginLeft(element, Math.Max(left, 0));
                LayoutContainer.SetMarginTop(element, Math.Max(top, 0));

                if (control.Anchor2.HasValue) {
                    if (control.Anchor2.Value.X < control.Anchor1.Value.X ||
                        control.Anchor2.Value.Y < control.Anchor1.Value.Y) {
                        _sawmill.Warning($"Invalid anchor2 value in DMF for element {control.Id}. Ignoring.");
                    } else {
                        int offset2X = elementPos.X + elementSize.X -
                                       anchorTo.X * control.Anchor2.Value.X / 100;
                        int offset2Y = elementPos.Y + elementSize.Y -
                                       anchorTo.Y * control.Anchor2.Value.Y / 100;
                        float width = _canvas.Width * control.Anchor2.Value.X / 100 + offset2X - left;
                        float height = _canvas.Height * control.Anchor2.Value.Y / 100 + offset2Y - top;
                        element.SetWidth = Math.Max(width, 0);
                        element.SetHeight = Math.Max(height, 0);
                    }
                }
            }
        }
    }

    /// <summary>
    ///     Updates the control's anchoring position to the window's current size.
    ///     Also updates other controls' anchoring position if they have a size of 0.
    /// </summary>
    /// <param name="control">The control triggering the anchor update</param>
    public void UpdateAnchorPosition(InterfaceControl control) {
        control.AnchorPosition = _canvas.PixelSize;

        // Also update the anchor position for anything with a size of 0
        foreach (InterfaceControl child in ChildControls) {
            if (child.UIElement.SetWidth == 0)
                child.AnchorPosition = child.AnchorPosition with {X = _canvas.PixelWidth + child.Size.X};
            if (child.UIElement.SetHeight == 0)
                child.AnchorPosition = child.AnchorPosition with {Y = _canvas.PixelHeight + child.Size.Y};
        }

        UpdateAnchors();
    }

    private void UpdateWindowAttributes((OSWindow? osWindow, IClydeWindow? clydeWindow) windowRoot) {
        // TODO: this would probably be cleaner if an OSWindow for MainWindow was available.
        (OSWindow? osWindow, IClydeWindow? clydeWindow) = windowRoot;

        //if our window is null or closed, we need to create a new one. Otherwise we need to update the existing one.
        if (osWindow == null && clydeWindow == null) {
            CreateWindow();
            return; //we return because CreateWindow() calls UpdateWindowAttributes() again.
        }

        if (osWindow != null) osWindow.Title = Title;
        else if (clydeWindow != null) clydeWindow.Title = Title;

        WindowRoot? root = null;
        if (osWindow?.Window != null)
            root = _uiMgr.GetWindowRoot(osWindow.Window);
        else if (clydeWindow != null)
            root = _uiMgr.GetWindowRoot(clydeWindow);

        if (root != null)
            root.BackgroundColor = WindowDescriptor.BackgroundColor.Value != Color.Transparent
                ? WindowDescriptor.BackgroundColor.Value
                : DreamStylesheet.DefaultBackgroundColor;

        if (osWindow is { ClydeWindow: not null })
            osWindow.ClydeWindow.IsVisible = WindowDescriptor.IsVisible.Value;
        else if (clydeWindow != null) clydeWindow.IsVisible = WindowDescriptor.IsVisible.Value;
    }

    public void CreateChildControls() {
        foreach (ControlDescriptor controlDescriptor in WindowDescriptor.ControlDescriptors)
            AddChild(controlDescriptor);
    }

    public override void AddChild(ElementDescriptor descriptor) {
        if (descriptor is not ControlDescriptor controlDescriptor)
            throw new Exception($"Attempted to add {descriptor} to a window, but it was not a control");
        if (controlDescriptor is WindowDescriptor)
            throw new Exception("Cannot add a window to a window");

        InterfaceControl control = controlDescriptor switch {
            ControlDescriptorChild => new ControlChild(controlDescriptor, this),
            ControlDescriptorInput => new ControlInput(controlDescriptor, this),
            ControlDescriptorButton => new ControlButton(controlDescriptor, this),
            ControlDescriptorOutput => new ControlOutput(controlDescriptor, this),
            ControlDescriptorInfo => new ControlInfo(controlDescriptor, this),
            ControlDescriptorMap => new ControlMap(controlDescriptor, this),
            ControlDescriptorBrowser => new ControlBrowser(controlDescriptor, this),
            ControlDescriptorLabel => new ControlLabel(controlDescriptor, this),
            ControlDescriptorGrid => new ControlGrid(controlDescriptor, this),
            ControlDescriptorTab => new ControlTab(controlDescriptor, this),
            ControlDescriptorBar => new ControlBar(controlDescriptor, this),
            _ => throw new Exception($"Invalid descriptor {controlDescriptor.GetType()}")
        };

        // Can't have out-of-order components, so make sure they're ordered properly
        if (ChildControls.Count > 0) {
            DMFPropertyPos prevPos = ChildControls[^1].Pos;
            DMFPropertyPos curPos = control.Pos;
            if (prevPos.X <= curPos.X && prevPos.Y <= curPos.Y) {
                ChildControls.Add(control);
            } else {
                _sawmill.Warning(
                    $"Out of order component {control.Id}. Elements should be defined in order of position. Attempting to fix automatically.");

                var i = 0;
                while (i < ChildControls.Count) {
                    prevPos = ChildControls[i].Pos;
                    if (prevPos.X <= curPos.X && prevPos.Y <= curPos.Y)
                        i++;
                    else
                        break;
                }

                ChildControls.Insert(i, control);
            }
        } else {
            ChildControls.Add(control);
        }

        _canvas.Children.Add(control.UIElement);
    }

    // Because of how windows are not always real windows,
    // UIControl contains the *contents* of the window, not the actual OS window itself.
    protected override Control CreateUIElement() {
        var container = new BoxContainer {
            RectClipContent = true,
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            Children = {
                (_menuContainer = new PanelContainer {
                    PanelOverride = new StyleBoxFlat(Color.White),
                    HorizontalExpand = true
                }),
                (_canvas = new LayoutContainer {
                    InheritChildMeasure = false,
                    VerticalExpand = true
                })
            }
        };

        _canvas.OnResized += CanvasOnResized;

        return container;
    }

    private void CanvasOnResized() {
        UpdateAnchors();
    }

    public override bool TryGetProperty(string property, [NotNullWhen(true)] out IDMFProperty? value) {
        switch (property) {
            case "size": // ControlWindow has its own getter for this because it doesn't use SetSize
                value = new DMFPropertySize(UIElement.Size);
                return true;
            case "inner-size":
                value = new DMFPropertySize((int)_canvas.Width, (int)_canvas.Height);
                return true;
            case "outer-size":
                if (_myWindow.osWindow is not null) {
                    value = new DMFPropertySize((int)_myWindow.osWindow.Width, (int)_myWindow.osWindow.Height);
                    return true;
                }

                if (_myWindow.clydeWindow is not null) {
                    value = new DMFPropertySize(_myWindow.clydeWindow.Size);
                    return true;
                }

                value = new DMFPropertySize(UIElement.Size);
                return true;
            case "is-minimized":
                if (_myWindow.osWindow?.ClydeWindow != null) {
                    value = new DMFPropertyBool(_myWindow.osWindow.ClydeWindow.IsMinimized);
                    return true;
                }

                if (_myWindow.clydeWindow is not null) {
                    value = new DMFPropertyBool(_myWindow.clydeWindow.IsMinimized);
                    return true;
                }

                value = new DMFPropertyBool(false);
                return true;
            case "is-maximized": //TODO this is currently "not isMinimised" because RT doesn't expose a maximised check
                if (_myWindow.osWindow?.ClydeWindow != null) {
                    value = new DMFPropertyBool(!_myWindow.osWindow.ClydeWindow.IsMinimized);
                    return true;
                }

                if (_myWindow.clydeWindow is not null) {
                    value = new DMFPropertyBool(!_myWindow.clydeWindow.IsMinimized);
                    return true;
                }

                value = new DMFPropertyBool(false);
                return true;
            default:
                return base.TryGetProperty(property, out value);
        }
    }

    public override void SetProperty(string property, string value, bool manualWinset = false) {
        switch (property) {
            case "size":
                if (_myWindow.osWindow is {ClydeWindow: not null}) {
                    var size = new DMFPropertySize(value);
                    float uiScale = _myWindow.osWindow.UIScale;
                    size.X = (int)(size.X * uiScale); // TODO: RT should probably do this itself
                    size.Y = (int)(size.Y * uiScale);
                    _myWindow.osWindow.ClydeWindow.Size = size.Vector;
                }

                return;
            case "pos":
                // TODO: RT offers no ability to position windows
                return;
        }

        base.SetProperty(property, value, manualWinset);
    }

    // TODO: This needs to bubble up through to parent windows
    public void SetStatus(string status) {
        if (_currentStatus == status)
            return;

        _currentStatus = status;

        string onStatusCommand = WindowDescriptor.OnStatus.AsRaw();
        if (string.IsNullOrWhiteSpace(onStatusCommand))
            return;

        onStatusCommand = onStatusCommand.Replace("[[*]]", new DMFPropertyString(status).AsArg());
        InterfaceManager.RunCommand(onStatusCommand);
    }
}
