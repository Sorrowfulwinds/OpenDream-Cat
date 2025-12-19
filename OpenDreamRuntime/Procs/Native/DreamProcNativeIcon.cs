using OpenDreamRuntime.Objects;
using OpenDreamRuntime.Objects.Types;
using OpenDreamRuntime.Resources;
using OpenDreamShared.Dream;
using BlendType = OpenDreamRuntime.Objects.DreamIconOperationBlend.BlendType;
using DreamValueTypeFlag = OpenDreamRuntime.DreamValue.DreamValueTypeFlag;

namespace OpenDreamRuntime.Procs.Native {
    internal static class DreamProcNativeIcon {
        [DreamProc("Width")]
        public static DreamValue NativeProc_Width(NativeProc.Bundle bundle, DreamObject? src, DreamObject? usr) {
            return new DreamValue(((DreamObjectIcon)src!).Icon.Width);
        }

        [DreamProc("Height")]
        public static DreamValue NativeProc_Height(NativeProc.Bundle bundle, DreamObject? src, DreamObject? usr) {
            return new DreamValue(((DreamObjectIcon)src!).Icon.Height);
        }

        [DreamProc("Insert")]
        [DreamProcParameter("new_icon", Type = DreamValueTypeFlag.DreamObject)]
        [DreamProcParameter("icon_state", Type = DreamValueTypeFlag.String)]
        [DreamProcParameter("dir", Type = DreamValueTypeFlag.Float)]
        [DreamProcParameter("frame", Type = DreamValueTypeFlag.Float)]
        [DreamProcParameter("moving", Type = DreamValueTypeFlag.Float)]
        [DreamProcParameter("delay", Type = DreamValueTypeFlag.Float)]
        public static DreamValue NativeProc_Insert(NativeProc.Bundle bundle, DreamObject? src, DreamObject? usr) {
            //TODO Figure out what happens when you pass the wrong types as args

            DreamValue newIcon = bundle.GetArgument(0, "new_icon");
            DreamValue iconState = bundle.GetArgument(1, "icon_state");
            DreamValue dir = bundle.GetArgument(2, "dir");
            DreamValue frame = bundle.GetArgument(3, "frame");
            DreamValue moving = bundle.GetArgument(4, "moving");
            DreamValue delay = bundle.GetArgument(5, "delay");

            // TODO: moving & delay

            var resourceManager = IoCManager.Resolve<DreamResourceManager>();
            if (!resourceManager.TryLoadIcon(newIcon, out var iconRsc))
                throw new Exception($"Cannot insert {newIcon}");

            ((DreamObjectIcon)src!).Icon.InsertStates(iconRsc, iconState, dir, frame); // TODO: moving & delay
            return DreamValue.Null;
        }

        public static void Blend(DreamIcon icon, DreamValue blend, BlendType function, int x, int y) {
            if (blend.TryGetValueAsString(out var colorStr)) {
                if (!ColorHelpers.TryParseColor(colorStr, out var color))
                    throw new Exception($"Invalid color {colorStr}");

                icon.ApplyOperation(new DreamIconOperationBlendColor(function, x, y, color));
            } else {
                icon.ApplyOperation(new DreamIconOperationBlendImage(function, x, y, blend));
            }
        }

        [DreamProc("Blend")]
        [DreamProcParameter("icon", Type = DreamValueTypeFlag.DreamObject)]
        [DreamProcParameter("function", Type = DreamValueTypeFlag.Float, DefaultValue = (int)BlendType.Add)] // ICON_ADD
        [DreamProcParameter("x", Type = DreamValueTypeFlag.Float, DefaultValue = 1)]
        [DreamProcParameter("y", Type = DreamValueTypeFlag.Float, DefaultValue = 1)]
        public static DreamValue NativeProc_Blend(NativeProc.Bundle bundle, DreamObject? src, DreamObject? usr) {
            //TODO Figure out what happens when you pass the wrong types as args

            DreamValue icon = bundle.GetArgument(0, "icon");
            DreamValue function = bundle.GetArgument(1, "function");

            bundle.GetArgument(2, "x").TryGetValueAsInteger(out var x);
            bundle.GetArgument(3, "y").TryGetValueAsInteger(out var y);

            if (!function.TryGetValueAsInteger(out var functionValue))
                throw new Exception($"Invalid 'function' argument {function}");

            Blend(((DreamObjectIcon)src!).Icon, icon, (BlendType)functionValue, x, y);
            return DreamValue.Null;
        }

        [DreamProc("Scale")]
        [DreamProcParameter("width", Type = DreamValueTypeFlag.Float)]
        [DreamProcParameter("height", Type = DreamValueTypeFlag.Float)]
        public static DreamValue NativeProc_Scale(NativeProc.Bundle bundle, DreamObject? src, DreamObject? usr) {
            //TODO Figure out what happens when you pass the wrong types as args

            bundle.GetArgument(0, "width").TryGetValueAsInteger(out var width);
            bundle.GetArgument(1, "height").TryGetValueAsInteger(out var height);

            DreamIcon iconObj = ((DreamObjectIcon)src!).Icon;
            iconObj.Width = width;
            iconObj.Height = height;
            return DreamValue.Null;
        }

        [DreamProc("Turn")]
        [DreamProcParameter("angle", Type = DreamValueTypeFlag.Float)]
        public static DreamValue NativeProc_Turn(NativeProc.Bundle bundle, DreamObject? src, DreamObject? usr) {
            DreamValue angleArg = bundle.GetArgument(0, "angle");
            if (!angleArg.TryGetValueAsFloat(out float angle)) {
                return new DreamValue(src!); // Defaults to input on invalid angle
            }

            _NativeProc_TurnInternal((DreamObjectIcon)src!, angle);
            return DreamValue.Null;
        }

        /// <summary> Turns a given icon a given amount of degrees clockwise. </summary>
        public static void _NativeProc_TurnInternal(DreamObjectIcon src, float angle) {
            src.Turn(angle);
        }

        [DreamProc("GetPixel")]
        [DreamProcParameter("x", Type = DreamValueTypeFlag.Float)]
        [DreamProcParameter("y", Type = DreamValueTypeFlag.Float)]
        [DreamProcParameter("icon_state", Type = DreamValueTypeFlag.String, DefaultValue = "")]
        [DreamProcParameter("dir", Type = DreamValueTypeFlag.Float, DefaultValue = 0)]
        [DreamProcParameter("frame", Type = DreamValueTypeFlag.Float, DefaultValue = 1)]
        [DreamProcParameter("moving", Type = DreamValueTypeFlag.Float, DefaultValue = -1)]
        public static DreamValue NativeProc_GetPixel(NativeProc.Bundle bundle, DreamObject? src, DreamObject? usr) {
            //X or Y bigger than sprites gives no return
            //Non-existent icon_state gives no return, blank string "" targets 'no name' icon first, then the first icon in the dmi otherwise, no input e.g GetPixel(x,y) acts the same.
            //dir is checked by binary flags, meaning any number of numbers might hit a return. Non-existent dirs seems to cause an exception on launch. ([launched] Child exited with code 0xc0000005.)
            //frame is 1-indexed, but 0 also counts as 1. Numbers greater than the number of frames give no return
            /*moving says it defaults to -1, but in text it defaults to null?? X>0 only targets icon_state named icons that are marked as moving. X=0 for non-moving.
            If you target a moving state that doesnt exist and icon_state is empty-string it targets the first icon in the file irrelevant of movement state, otherwise no return.
            Null && X<0 target both states, first icon_state in the dmi wins. Any number below 0 works.
            */
            var x = bundle.GetArgument(0, "x").MustGetValueAsInteger();
            var y = bundle.GetArgument(1, "y").MustGetValueAsInteger();
            var iconState = bundle.GetArgument(2, "icon_state").MustGetValueAsString();
            var dir = bundle.GetArgument(3, "dir").MustGetValueAsInteger();
            var frame = bundle.GetArgument(4, "frame").MustGetValueAsInteger();
            if (frame == 0) //1-indexed, but 0 also counts as 1.
                frame = 1;
            var moving = -1;
            if (!bundle.GetArgument(4, "moving").IsNull) { //Null counts as -1, crush arbitrary values to specifics.
                moving = bundle.GetArgument(5, "moving").MustGetValueAsInteger() switch {
                    <0 => -1, //Moving & Non-Moving states
                    0  =>  0, //Non-Moving states
                    >0 =>  1  //Moving states
                };
            }

            //Icon > dictionary 'states' of (string, iconstates), width, height
            //Iconstate > Dictionary<AtomDirection, List<IconFrame>> Directions
            //List of IconFrame > Image, Width, Height,
            DreamIcon iconObj = ((DreamObjectIcon)src!).Icon;
            DreamIcon.IconState iconStatePull = iconObj.States[iconState]; //use .TryGetValue(iconState, out varname)
            List<DreamIcon.IconFrame> frames = iconStatePull.Frames[dir]; //need helper for int/dreamint > AtomDirection
            DreamIcon.IconFrame iconFramePull = frames[frame];
            Image<Rgba32>? theImageWeWant = iconFramePull.Image;

            return 0;
        }
    }
}
