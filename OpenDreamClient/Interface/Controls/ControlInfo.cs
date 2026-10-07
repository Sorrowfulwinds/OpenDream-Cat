using System.Linq;
using OpenDreamClient.Input;
using OpenDreamClient.Interface.Controls.UI;
using OpenDreamClient.Interface.Html;
using OpenDreamShared.Dream;
using OpenDreamShared.Interface.Descriptors;
using OpenDreamShared.Network.Messages;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Input;
using Robust.Shared.Network;
using Robust.Shared.Utility;

namespace OpenDreamClient.Interface.Controls;

[Virtual]
internal class InfoPanel : Control {
    protected InfoPanel(string name) {
        PanelName = name;
        TabContainer.SetTabTitle(this, name);
    }

    public string PanelName { get; }

    public virtual void UpdateElementDescriptor(ControlDescriptorInfo descriptor) {
    }
}

internal sealed class StatPanel : InfoPanel {
    private readonly IEntitySystemManager _entitySystemManager;
    private readonly List<StatEntry> _entries = new();
    private readonly GridContainer _grid;

    private readonly ControlInfo _owner;

    public StatPanel(ControlInfo owner, IEntitySystemManager entitySystemManager, string name) : base(name) {
        _owner = owner;
        _entitySystemManager = entitySystemManager;
        _grid = new GridContainer {
            Columns = 2
        };

        var scrollViewer = new ScrollContainer {
            HScrollEnabled = false,
            Children = {_grid}
        };

        AddChild(scrollViewer);
    }

    public override void UpdateElementDescriptor(ControlDescriptorInfo descriptor) {
        base.UpdateElementDescriptor(descriptor);
        Color textColor = descriptor.TextColor.Value != Color.Transparent ? descriptor.TextColor.Value : Color.Black;
        foreach (StatEntry entry in _entries) entry.SetTextColor(textColor);
    }

    public void UpdateLines(List<(string Name, string Value, string? AtomRef)> lines) {
        for (var i = 0; i < Math.Max(_entries.Count, lines.Count); i++) {
            StatEntry entry = GetEntry(i);

            if (i < lines.Count) {
                (string Name, string Value, string? AtomRef) line = lines[i];

                entry.SetLabels(line.Name, line.Value, line.AtomRef);
            } else {
                entry.Clear();
            }
        }
    }

    private StatEntry GetEntry(int index) {
        // Expand the entries if there aren't enough
        if (_entries.Count <= index)
            for (int i = _entries.Count; i <= index; i++) {
                var entry = new StatEntry(_owner, _entitySystemManager);

                _grid.AddChild(entry.NameLabel);
                _grid.AddChild(entry.ValueLabel);
                _entries.Add(entry);
            }

        return _entries[index];
    }

    private sealed class StatEntry {
        public readonly RichTextLabel NameLabel = new();
        public readonly RichTextLabel ValueLabel = new();
        private readonly IEntitySystemManager _entitySystemManager;
        private readonly FormattedMessage _nameText = new();

        private readonly ControlInfo _owner;
        private readonly FormattedMessage _valueText = new();
        private string? _atomRef;
        private string _name = string.Empty;
        private Color _textColor = Color.Black;
        private string _value = string.Empty;

        public StatEntry(ControlInfo owner, IEntitySystemManager entitySystemManager) {
            _owner = owner;
            _entitySystemManager = entitySystemManager;

            // TODO: Change color when the mouse is hovering (if clickable)
            //       I couldn't find a way to do this without recreating the FormattedMessage
            ValueLabel.MouseFilter = MouseFilterMode.Stop;
            ValueLabel.OnKeyBindDown += OnKeyBindDown;
            if (_owner.InfoDescriptor.TextColor.Value != Color.Black)
                _textColor = _owner.InfoDescriptor.TextColor.Value;
        }

        public void Clear() {
            _atomRef = null;
            _nameText.Clear();
            _valueText.Clear();

            NameLabel.SetMessage(_nameText);
            ValueLabel.SetMessage(_valueText);
        }

        public void SetTextColor(Color textColor) {
            if (_textColor == textColor)
                return;

            _textColor = textColor;
            UpdateLabels();
        }

        public void SetLabels(string name, string value, string? atomRef) {
            // TODO: Tabs should align with each other.
            //       Probably should be done by RT, but it just ignores them currently.
            _name = name.Replace("\t", "    ");
            _value = value.Replace("\t", "    ");
            _atomRef = atomRef;

            UpdateLabels();
        }

        private void UpdateLabels() {
            _nameText.Clear();
            _valueText.Clear();

            // Use the default color and font
            _nameText.PushColor(_textColor);
            _valueText.PushColor(_textColor);
            _nameText.PushTag(new MarkupNode("font", null, null));
            _valueText.PushTag(new MarkupNode("font", null, null));

            if (_owner.InfoDescriptor.AllowHtml.Value) {
                // TODO: Look into using RobustToolbox's markup parser once it's customizable enough
                HtmlParser.Parse(_name, _nameText);
                HtmlParser.Parse(_value, _valueText);
            } else {
                _nameText.AddText(_name);
                _valueText.AddText(_value);
            }

            NameLabel.SetMessage(_nameText);
            ValueLabel.SetMessage(_valueText);
        }

        private void OnKeyBindDown(GUIBoundKeyEventArgs e) {
            if (e.Function != EngineKeyFunctions.Use && e.Function != OpenDreamKeyFunctions.MouseMiddle &&
                e.Function != EngineKeyFunctions.TextCursorSelect)
                return;
            if (_atomRef == null)
                return;
            if (!_entitySystemManager.TryGetEntitySystem(out MouseInputSystem? mouseInputSystem))
                return;

            e.Handle();
            mouseInputSystem.HandleStatClick(_atomRef, e.Function == EngineKeyFunctions.UIRightClick,
                e.Function == OpenDreamKeyFunctions.MouseMiddle);
        }
    }
}

internal sealed partial class VerbPanel : InfoPanel {
    public static readonly string DefaultVerbPanel = "Verbs"; // TODO: default_verb_category

    private readonly VerbPanelGrid _grid;
    private readonly Dictionary<(int VerbId, ClientObjectReference Src), Button> _verbButtons = new();
    private readonly ClientVerbSystem? _verbSystem;

    [Dependency] private IEntitySystemManager _entitySystemManager = default!;

    private Color _highlightColor;
    private Color _textColor;

    public VerbPanel(string name) : base(name) {
        IoCManager.InjectDependencies(this);
        _entitySystemManager.TryGetEntitySystem(out _verbSystem);

        var scrollContainer = new ScrollContainer {
            HScrollEnabled = false
        };

        _grid = new VerbPanelGrid {
            VerticalAlignment = VAlignment.Top
        };

        scrollContainer.AddChild(_grid);
        AddChild(scrollContainer);
    }

    public override void UpdateElementDescriptor(ControlDescriptorInfo descriptor) {
        base.UpdateElementDescriptor(descriptor);

        _highlightColor = descriptor.HighlightColor.Value;
        _textColor = descriptor.TextColor.Value != Color.Transparent ? descriptor.TextColor.Value : Color.Black;

        foreach (Control child in _grid.Children) {
            if (child is not Button button)
                continue;

            button.Label.FontColorOverride = _textColor;
        }
    }

    public void RefreshVerbs(IEnumerable<(int, ClientObjectReference, VerbSystem.VerbInfo)> verbs) {
        IOrderedEnumerable<(int, ClientObjectReference, VerbSystem.VerbInfo)> panelVerbs = verbs
            .Where(v => v.Item3.GetCategoryOrDefault(DefaultVerbPanel) == PanelName)
            .Order(VerbNameComparer.OrdinalInstance);

        var seenKeys = new HashSet<(int, ClientObjectReference)>();
        var gridIndex = 0;
        foreach ((int verbId, ClientObjectReference src, VerbSystem.VerbInfo verbInfo) in panelVerbs) {
            (int verbId, ClientObjectReference src) key = (verbId, src);
            seenKeys.Add(key);

            if (!_verbButtons.ContainsKey(key)) {
                var verbButton = new Button {
                    Margin = new Thickness(2),
                    Text = verbInfo.Name,
                    TextAlign = Label.AlignMode.Center
                };

                verbButton.Label.Margin = new Thickness(6, 0, 6, 2);
                verbButton.Label.FontColorOverride = _textColor;
                verbButton.StyleBoxOverride = new StyleBoxEmpty();

                verbButton.OnButtonDown += _ => { _verbSystem?.ExecuteVerb(src, verbId); };

                verbButton.OnMouseEntered += _ => { verbButton.Label.FontColorOverride = _highlightColor; };

                verbButton.OnMouseExited += _ => { verbButton.Label.FontColorOverride = _textColor; };

                _verbButtons[key] = verbButton;
                _grid.AddChild(verbButton);
                verbButton.SetPositionInParent(gridIndex);
            }

            gridIndex++;
        }

        foreach ((int VerbId, ClientObjectReference Src) key in _verbButtons.Keys) {
            if (seenKeys.Contains(key)) continue;

            _grid.RemoveChild(_verbButtons[key]);
            _verbButtons.Remove(key);
        }
    }
}

public sealed partial class ControlInfo : InterfaceControl {
    public static readonly string StyleClassDMFInfo = "DMFInfo";
    private readonly Dictionary<string, StatPanel> _statPanels = new();
    private readonly SortedDictionary<string, VerbPanel> _verbPanels = new();

    private PanelContainer _container;

    private bool _defaultPanelSent;
    [Dependency] private IEntitySystemManager _entitySystemManager = default!;

    [Dependency] private IClientNetManager _netManager = default!;
    private TabContainer _tabControl;

    public ControlInfo(ControlDescriptor controlDescriptor, ControlWindow window) : base(controlDescriptor, window) {
        IoCManager.InjectDependencies(this);
    }

    public ControlDescriptorInfo InfoDescriptor => (ControlDescriptorInfo)ControlDescriptor;

    protected override Control CreateUIElement() {
        _container = new PanelContainer {
            Children = {
                (_tabControl = new TabContainer())
            },
            StyleClasses = {StyleClassDMFInfo}
        };

        _tabControl.OnTabChanged += OnSelectionChanged;

        _tabControl.OnVisibilityChanged += args => {
            if (args.Visible)
                OnShowEvent();
            else
                OnHideEvent();
        };

        if (ControlDescriptor.IsVisible.Value)
            OnShowEvent();
        else
            OnHideEvent();

        return _container;
    }

    protected override void UpdateElementDescriptor() {
        base.UpdateElementDescriptor();

        _container.PanelOverride = InfoDescriptor.TabBackgroundColor.Value != Color.Transparent
            ? new StyleBoxFlat(InfoDescriptor.TabBackgroundColor.Value)
            : null;
        _tabControl.PanelStyleBoxOverride = new StyleBoxInfoPanel(
            InfoDescriptor.BackgroundColor.Value != Color.Transparent
                ? InfoDescriptor.BackgroundColor.Value
                : Color.White);
        _tabControl.TabFontColorOverride = InfoDescriptor.TabTextColor.Value != Color.Transparent
            ? InfoDescriptor.TabTextColor.Value
            : null;
        _tabControl.TabFontColorInactiveOverride = InfoDescriptor.TabTextColor.Value != Color.Transparent
            ? InfoDescriptor.TabTextColor.Value
            : null;

        foreach (StatPanel panel in _statPanels.Values)
            panel.UpdateElementDescriptor(InfoDescriptor);
        foreach (VerbPanel panel in _verbPanels.Values)
            panel.UpdateElementDescriptor(InfoDescriptor);
    }

    public void RefreshVerbs(ClientVerbSystem verbSystem) {
        IEnumerable<(int, ClientObjectReference, VerbSystem.VerbInfo)> verbs = verbSystem.GetExecutableVerbs();

        foreach ((int _, ClientObjectReference _, VerbSystem.VerbInfo verb) in verbs) {
            string category = verb.GetCategoryOrDefault(VerbPanel.DefaultVerbPanel);

            if (!HasVerbPanel(category)) CreateVerbPanel(category);
        }

        foreach (KeyValuePair<string, VerbPanel> panel in _verbPanels) _verbPanels[panel.Key].RefreshVerbs(verbs);
    }

    public void SelectStatPanel(string statPanelName) {
        if (_statPanels.TryGetValue(statPanelName, out StatPanel? panel))
            _tabControl.CurrentTab = panel.GetPositionInParent();
    }

    public void UpdateStatPanels(MsgUpdateStatPanels pUpdateStatPanels) {
        //Remove any panels the packet doesn't contain
        foreach (KeyValuePair<string, StatPanel> existingPanel in _statPanels)
            if (!pUpdateStatPanels.StatPanels.ContainsKey(existingPanel.Key)) {
                _tabControl.RemoveChild(existingPanel.Value);
                _statPanels.Remove(existingPanel.Key);
            }

        foreach (KeyValuePair<string, List<(string Name, string Value, string? AtomRef)>> updatingPanel in
                 pUpdateStatPanels.StatPanels) {
            if (!_statPanels.TryGetValue(updatingPanel.Key, out StatPanel? panel))
                panel = CreateStatPanel(updatingPanel.Key);

            panel.UpdateLines(updatingPanel.Value);
        }

        // Tell the server we're ready to receive data
        if (!_defaultPanelSent && _tabControl.ChildCount > 0) {
            var msg = new MsgSelectStatPanel {
                StatPanel = _tabControl.GetActualTabTitle(0)
            };

            _netManager.ClientSendMessage(msg);
            _defaultPanelSent = true;
        }
    }

    public bool HasVerbPanel(string name) {
        return _verbPanels.ContainsKey(name);
    }

    public void CreateVerbPanel(string name) {
        var panel = new VerbPanel(name);
        panel.UpdateElementDescriptor(InfoDescriptor);
        _verbPanels.Add(name, panel);
        SortPanels();
    }

    private StatPanel CreateStatPanel(string name) {
        var panel = new StatPanel(this, _entitySystemManager, name);
        panel.Margin = new Thickness(20, 2);
        panel.UpdateElementDescriptor(InfoDescriptor);
        _statPanels.Add(name, panel);
        SortPanels();
        return panel;
    }

    private void SortPanels() {
        _tabControl.Children.Clear();
        foreach ((string _, StatPanel statPanel) in _statPanels) _tabControl.AddChild(statPanel);

        foreach ((string _, VerbPanel verbPanel) in _verbPanels) _tabControl.AddChild(verbPanel);
    }

    private void OnSelectionChanged(int tabIndex) {
        var panel = (InfoPanel)_tabControl.GetChild(tabIndex);
        var msg = new MsgSelectStatPanel {
            StatPanel = panel.PanelName
        };

        _netManager.ClientSendMessage(msg);
    }

    public void OnShowEvent() {
        var controlDescriptor = (ControlDescriptorInfo)ControlDescriptor;
        if (!string.IsNullOrWhiteSpace(controlDescriptor.OnShowCommand.Value))
            InterfaceManager.RunCommand(controlDescriptor.OnShowCommand.AsRaw());
    }

    public void OnHideEvent() {
        var controlDescriptor = (ControlDescriptorInfo)ControlDescriptor;
        if (!string.IsNullOrWhiteSpace(controlDescriptor.OnHideCommand.Value))
            InterfaceManager.RunCommand(controlDescriptor.OnHideCommand.AsRaw());
    }
}

internal sealed class VerbNameComparer(bool ordinal) : IComparer<(int, ClientObjectReference, VerbSystem.VerbInfo)> {
    // Verbs are displayed alphabetically with uppercase coming first (BYOND behavior)
    public static VerbNameComparer OrdinalInstance = new(true);

    // Verbs are displayed alphabetically according to the user's culture
    public static VerbNameComparer CultureInstance = new(false);

    public int Compare((int, ClientObjectReference, VerbSystem.VerbInfo) a,
        (int, ClientObjectReference, VerbSystem.VerbInfo) b) {
        return string.Compare(a.Item3.Name, b.Item3.Name,
            ordinal ? StringComparison.Ordinal : StringComparison.CurrentCulture);
    }
}
