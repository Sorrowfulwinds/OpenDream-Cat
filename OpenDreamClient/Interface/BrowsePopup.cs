using OpenDreamClient.Interface.Controls;
using OpenDreamShared.Interface.Descriptors;
using OpenDreamShared.Interface.DMF;
using Robust.Client.Graphics;
using Robust.Client.UserInterface.Controls;

namespace OpenDreamClient.Interface;

internal sealed class BrowsePopup {
    public readonly ControlBrowser Browser;
    public readonly ControlWindow WindowElement;

    private readonly OSWindow _window;

    public BrowsePopup(
        string name,
        Vector2i size,
        IClydeWindow ownerWindow) {
        var popupWindowDescriptor = new WindowDescriptor(name,
            new List<ControlDescriptor> {
                new ControlDescriptorBrowser {
                    Id = new DMFPropertyString("browser"),
                    Size = new DMFPropertySize(size),
                    Anchor1 = new DMFPropertyPos(0, 0),
                    Anchor2 = new DMFPropertyPos(100, 100)
                }
            }) {
            Size = new DMFPropertySize(size)
        };

        WindowElement = new ControlWindow(popupWindowDescriptor);
        WindowElement.CreateChildControls();

        _window = WindowElement.CreateWindow();
        _window.StartupLocation = WindowStartupLocation.CenterOwner;
        _window.Owner = ownerWindow;
        _window.Closed += OnWindowClosed;

        Browser = (ControlBrowser)WindowElement.ChildControls[0];
    }

    public event Action? Closed;

    public void Open() {
        _window.Show();
        // _window.Focus();
    }

    public void Close() {
        _window.Close();
    }

    private void OnWindowClosed() {
        Closed?.Invoke();
    }
}
