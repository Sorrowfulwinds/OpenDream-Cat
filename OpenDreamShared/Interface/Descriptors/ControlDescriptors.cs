using System;
using System.Collections.Generic;
using JetBrains.Annotations;
using OpenDreamShared.Interface.DMF;
using Robust.Shared.Analyzers;
using Robust.Shared.Maths;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Manager.Attributes;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Value;

namespace OpenDreamShared.Interface.Descriptors;

[Virtual]
public partial class ControlDescriptor : ElementDescriptor {
    [DataField("anchor1")] public DMFPropertyPos? Anchor1;

    [DataField("anchor2")] public DMFPropertyPos? Anchor2;

    [DataField("background-color")] public DMFPropertyColor BackgroundColor = new(Color.Transparent);

    [DataField("border")] public DMFPropertyString Border = new("none");

    [DataField("drop-zone")] public DMFPropertyBool DropZone; //default varies

    [DataField("flash")] public DMFPropertyNum Flash = new(0);

    [DataField("focus")] public DMFPropertyBool Focus = new(false);

    [DataField("font-family")] public DMFPropertyString FontFamily = new("");

    [DataField("font-size")] public DMFPropertyNum FontSize = new(0);

    [DataField("font-style")] public DMFPropertyString FontStyle = new("");

    [DataField("is-default")] public DMFPropertyBool IsDefault = new(false);

    [DataField("is-disabled")] public DMFPropertyBool IsDisabled = new(false);

    [DataField("is-transparent")] public DMFPropertyBool IsTransparent = new(false);

    [DataField("is-visible")] public DMFPropertyBool IsVisible = new(true);

    [DataField("on-size")] public DMFPropertyString OnSize = new("");

    [DataField("pos")] public DMFPropertyPos Pos = new(0, 0);

    [DataField("right-click")] public DMFPropertyBool RightClick = new(false);

    [DataField("saved-params")] public DMFPropertyString SavedParams; //default varies

    [DataField("size")] public DMFPropertySize Size = new(0, 0);

    [DataField("text-color")] public DMFPropertyColor TextColor = new(Color.Black);
}

public sealed partial class WindowDescriptor : ControlDescriptor {
    public readonly List<ControlDescriptor> ControlDescriptors;

    [DataField("alpha")] public DMFPropertyNum Alpha = new(255);

    [DataField("can-close")] public DMFPropertyBool CanClose = new(true);

    [DataField("can-minimize")] public DMFPropertyBool CanMinimize = new(true);

    [DataField("can-resize")] public DMFPropertyBool CanResize = new(true);

    [DataField("can-scroll")] public DMFPropertyString CanScroll = new("none");

    [DataField("icon")] public DMFPropertyString Icon = new("");

    [DataField("image")] public DMFPropertyString Image = new("");

    [DataField("image-mode")] public DMFPropertyString ImageMode = new("stretch");

    [DataField("is-maximized")] public DMFPropertyBool IsMaximized = new(false);

    [DataField("is-minimized")] public DMFPropertyBool IsMinimized = new(false);

    [DataField("is-pane")] public DMFPropertyBool IsPane = new(false);

    [DataField("keep-aspect")] public DMFPropertyBool KeepAspect = new(false);

    [DataField("macro")] public DMFPropertyString Macro = new("");

    [DataField("menu")] public DMFPropertyString Menu = new("");

    [DataField("on-close")] public DMFPropertyString OnClose = new("");

    [DataField("on-status")] public DMFPropertyString OnStatus = new("");

    [DataField("statusbar")] public DMFPropertyBool StatusBar = new(true);

    [DataField("title")] public DMFPropertyString Title = new("");

    [DataField("titlebar")] public DMFPropertyBool TitleBar = new(true);

    [DataField("transparent-color")] public DMFPropertyColor TransparentColor = new(Color.Transparent);

    public WindowDescriptor(string id, List<ControlDescriptor>? controlDescriptors = null) {
        ControlDescriptors = controlDescriptors ?? new List<ControlDescriptor>();
        Id = new DMFPropertyString(id);
    }

    [UsedImplicitly]
    public WindowDescriptor() {
        ControlDescriptors = new List<ControlDescriptor>();
    }

    public override ControlDescriptor? CreateChildDescriptor(ISerializationManager serializationManager,
        MappingDataNode attributes) {
        if (!attributes.TryGet("type", out DataNode? elementType) || elementType is not ValueDataNode elementTypeValue)
            return null;

        if (elementTypeValue.Value == "MAIN") {
            attributes.Remove("id"); // Ignore the ID given to the MAIN element

            // Read the attributes into this descriptor
            serializationManager.Read(attributes, notNullableOverride: true, instanceProvider: () => this);
            return this;
        }

        Type? descriptorType = elementTypeValue.Value switch {
            "MAP" => typeof(ControlDescriptorMap),
            "CHILD" => typeof(ControlDescriptorChild),
            "OUTPUT" => typeof(ControlDescriptorOutput),
            "INFO" => typeof(ControlDescriptorInfo),
            "INPUT" => typeof(ControlDescriptorInput),
            "BUTTON" => typeof(ControlDescriptorButton),
            "BROWSER" => typeof(ControlDescriptorBrowser),
            "LABEL" => typeof(ControlDescriptorLabel),
            "GRID" => typeof(ControlDescriptorGrid),
            "TAB" => typeof(ControlDescriptorTab),
            "BAR" => typeof(ControlDescriptorBar),
            _ => null
        };

        if (descriptorType == null)
            return null;

        if (descriptorType == typeof(ControlDescriptorChild)) {
            // CHILD's top/bottom attributes alias to left/right
            // Code is duplicated in InterfaceElement.PopulateElementDescriptor()
            // TODO: A bit hacky. Remove this (may be worth abandoning RT's serialization manager)
            if (attributes.TryGet("top", out DataNode? topValue))
                attributes["left"] = topValue;
            if (attributes.TryGet("bottom", out DataNode? bottomValue))
                attributes["right"] = bottomValue;
        }

        var child = (ControlDescriptor?)serializationManager.Read(descriptorType, attributes);
        if (child == null)
            return null;

        ControlDescriptors.Add(child);
        return child;
    }

    public override ElementDescriptor CreateCopy(ISerializationManager serializationManager, string id) {
        WindowDescriptor copy = serializationManager.CreateCopy(this, notNullableOverride: true);

        copy._id = new DMFPropertyString(id);
        foreach (ControlDescriptor child in ControlDescriptors)
            copy.ControlDescriptors.Add(serializationManager.CreateCopy(child, notNullableOverride: false));
        return copy;
    }

    public WindowDescriptor WithVisible(ISerializationManager serializationManager, bool visible) {
        var copy = (WindowDescriptor)CreateCopy(serializationManager, Id.AsRaw());

        copy.IsVisible = new DMFPropertyBool(visible);
        return copy;
    }
}

public sealed partial class ControlDescriptorChild : ControlDescriptor {
    [DataField("is-vert")] public DMFPropertyBool IsVert = new(false);

    [DataField("left")] public DMFPropertyString Left = new("");

    [DataField("lock")] public DMFPropertyString Lock = new("none");

    [DataField("right")] public DMFPropertyString Right = new("");

    [DataField("show-splitter")] public DMFPropertyBool ShowSplitter = new(true);

    [DataField("splitter")] public DMFPropertyNum Splitter = new(50f);
}

public sealed partial class ControlDescriptorInput : ControlDescriptor {
    [DataField("command")] public DMFPropertyString Command = new("");

    [DataField("is-password")] public DMFPropertyBool IsPassword = new(false);

    [DataField("multi-line")] public DMFPropertyBool MultiLine = new(false);

    [DataField("no-command")] public DMFPropertyBool NoCommand = new(false);

    [DataField("text")] public DMFPropertyString Text = new("");
}

public sealed partial class ControlDescriptorButton : ControlDescriptor {
    [DataField("button-type")] public DMFPropertyString ButtonType = new("pushbutton");

    [DataField("command")] public DMFPropertyString Command = new("");

    [DataField("group")] public DMFPropertyString Group = new("");

    [DataField("image")] public DMFPropertyString Image = new("");

    [DataField("is-checked")] public DMFPropertyBool IsChecked = new(false);

    [DataField("is-flat")] public DMFPropertyBool IsFlat = new(false);

    [DataField("text")] public DMFPropertyString Text = new("");
}

public sealed partial class ControlDescriptorOutput : ControlDescriptor {
    [DataField("enable-http-images")] public DMFPropertyBool EnableHttpImages = new(false);

    [DataField("image")] public DMFPropertyString Image = new("");

    [DataField("legacy-size")] public DMFPropertyBool LegacySize = new(false);

    [DataField("link-color")] public DMFPropertyColor LinkColor = new(Color.Blue);

    [DataField("max-lines")] public DMFPropertyNum MaxLines = new(1000);

    [DataField("style")] public DMFPropertyString Style = new("");

    [DataField("visited-color")] public DMFPropertyColor VisitedColor = new(Color.Purple);
}

public sealed partial class ControlDescriptorInfo : ControlDescriptor {
    [DataField("allow-html")]
    public DMFPropertyBool
        AllowHtml = new(true); // Supposedly false by default, but it isn't if you're not using BYOND's default skin

    [DataField("highlight-color")] public DMFPropertyColor HighlightColor = new(Color.Green);

    [DataField("multi-line")] public DMFPropertyBool MultiLine = new(true);

    [DataField("on-hide")] public DMFPropertyString OnHideCommand = new("");

    [DataField("on-show")] public DMFPropertyString OnShowCommand = new("");

    [DataField("prefix-color")] public DMFPropertyColor PrefixColor = new(Color.Transparent);

    [DataField("suffix-color")] public DMFPropertyColor SuffixColor = new(Color.Transparent);

    [DataField("tab-background-color")] public DMFPropertyColor TabBackgroundColor = new(Color.Transparent);

    [DataField("tab-font-family")] public DMFPropertyString TabFontFamily = new("");

    [DataField("tab-font-size")] public DMFPropertyNum TabFontSize = new(0);

    [DataField("tab-font-style")] public DMFPropertyString TabFontStyle = new("");

    [DataField("tab-text-color")] public DMFPropertyColor TabTextColor = new(Color.Transparent);
}

public sealed partial class ControlDescriptorMap : ControlDescriptor {
    [DataField("icon-size")] public DMFPropertyNum IconSize = new(0);

    [DataField("letterbox")] public DMFPropertyBool Letterbox = new(true);

    [DataField("on-hide")] public DMFPropertyString OnHideCommand = new("");

    [DataField("on-show")] public DMFPropertyString OnShowCommand = new("");

    [DataField("style")] public DMFPropertyString Style = new("");

    [DataField("text-mode")] public DMFPropertyBool TextMode = new(false);

    [DataField("view-size")] public DMFPropertyNum ViewSize = new(0);

    [DataField("zoom")] public DMFPropertyNum Zoom = new(0);

    [DataField("zoom-mode")] public DMFPropertyString ZoomMode = new("normal");
}

public sealed partial class ControlDescriptorBrowser : ControlDescriptor {
    [DataField("auto-format")] public DMFPropertyBool AutoFormat = new(true);

    [DataField("on-hide")] public DMFPropertyString OnHideCommand = new("");

    [DataField("on-show")] public DMFPropertyString OnShowCommand = new("");

    [DataField("show-history")] public DMFPropertyBool ShowHistory = new(false);

    [DataField("show-url")] public DMFPropertyBool ShowUrl = new(false);

    [DataField("use-title")] public DMFPropertyBool UseTitle = new(false);
}

public sealed partial class ControlDescriptorLabel : ControlDescriptor {
    [DataField("align")] public DMFPropertyString Align = new("center");

    [DataField("image")] public DMFPropertyString Image = new("");

    [DataField("image-mode")] public DMFPropertyString ImageMode = new("stretch");

    [DataField("keep-aspect")] public DMFPropertyBool KeepAspect = new(false);

    [DataField("text")] public DMFPropertyString Text = new("");

    [DataField("text-wrap")] public DMFPropertyBool TextWrap = new(false);
}

public sealed partial class ControlDescriptorGrid : ControlDescriptor {
    [DataField("cell-span")] public DMFPropertySize CellSpan = new(1, 1);

    [DataField("cells")] public DMFPropertySize Cells = new(0, 0);

    [DataField("current-cell")] public DMFPropertySize CurrentCell = new(0, 0);

    [DataField("enable-http-images")] public DMFPropertyBool EnableHttpImages = new(false);

    [DataField("highlight-color")] public DMFPropertyColor HighlightColor = new(Color.Green);

    [DataField("is-list")] public DMFPropertyBool IsList = new(false);

    [DataField("line-color")] public DMFPropertyColor LineColor = new("#c0c0c0");

    [DataField("link-color")] public DMFPropertyColor LinkColor = new(Color.Blue);

    [DataField("show-lines")] public DMFPropertyString ShowLines = new("both");

    [DataField("show-names")] public DMFPropertyBool ShowNames = new(true);

    [DataField("small-icons")] public DMFPropertyBool SmallIcons = new(false);

    [DataField("style")] public DMFPropertyString Style = new("");

    [DataField("visited-color")] public DMFPropertyColor VisitedCOlor = new(Color.Purple);
}

public sealed partial class ControlDescriptorTab : ControlDescriptor {
    [DataField("current-tab")] public DMFPropertyString CurrentTab = new("");

    [DataField("multi-line")] public DMFPropertyBool MultiLine = new(true);

    [DataField("on-tab")] public DMFPropertyString OnTab = new("");

    [DataField("tabs")] public DMFPropertyString Tabs = new("");
}

public sealed partial class ControlDescriptorBar : ControlDescriptor {
    [DataField("angle1")] public DMFPropertyNum Angle1 = new(0); //start angle

    [DataField("angle2")] public DMFPropertyNum Angle2 = new(180); //end angle

    [DataField("bar-color")]
    public DMFPropertyColor
        BarColor = new(Color.Transparent); //insanely, the default causes the bar not to render regardless of value

    [DataField("dir")]
    public DMFPropertyString Dir = new("east"); //valid values: north/east/south/west/clockwise/cw/counterclockwise/ccw

    [DataField("is-slider")] public DMFPropertyBool IsSlider = new(false);

    [DataField("on-change")] public DMFPropertyString OnChange = new("");

    [DataField("value")] public DMFPropertyNum Value = new(0f); //position of the progress bar

    [DataField("width")]
    public DMFPropertyNum
        Width = new(10); //width of the progress bar in pixels. In the default EAST dir, this is more accurately thought of as "height"
}
