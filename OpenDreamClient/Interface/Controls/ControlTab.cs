using System.Diagnostics.CodeAnalysis;
using System.Linq;
using OpenDreamShared.Interface.Descriptors;
using OpenDreamShared.Interface.DMF;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;

namespace OpenDreamClient.Interface.Controls;

internal sealed class ControlTab(ControlDescriptor controlDescriptor, ControlWindow window)
    : InterfaceControl(controlDescriptor, window) {
    private readonly List<ControlWindow> _tabs = new();

    private TabContainer _tab;
    private ControlDescriptorTab TabDescriptor => (ControlDescriptorTab)ElementDescriptor;

    protected override Control CreateUIElement() {
        _tab = new TabContainer();

        return _tab;
    }

    protected override void UpdateElementDescriptor() {
        base.UpdateElementDescriptor();

        _tabs.Clear();
        _tab.RemoveAllChildren();

        string[] tabIds = TabDescriptor.Tabs.Value.Split(',',
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        foreach (string tabId in tabIds) {
            if (!InterfaceManager.Windows.TryGetValue(tabId, out ControlWindow? pane))
                continue;

            TabContainer.SetTabTitle(pane.UIElement, pane.Title);
            _tab.AddChild(pane.UIElement);
            _tabs.Add(pane);
            if (TabDescriptor.CurrentTab.Value == pane.Title)
                _tab.CurrentTab = pane.UIElement.GetPositionInParent();
        }
    }

    public override bool TryGetProperty(string property, [NotNullWhen(true)] out IDMFProperty? value) {
        switch (property) {
            case "current-tab":
                Control currentTab = _tab.GetChild(_tab.CurrentTab);

                // The use of First() is kinda bad but hopefully this isn't large or performance critical
                value = _tabs.First(tab => tab.UIElement == currentTab).Id;
                return true;
            default:
                return base.TryGetProperty(property, out value);
        }
    }
}
