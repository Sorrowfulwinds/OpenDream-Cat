using Robust.Shared.Maths;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using OpenDreamShared.Dream;
using System.Globalization;

namespace OpenDreamShared.Resources;

public static class DMIParser {
    public static readonly AtomDirection[] DMIFrameDirections = {
        AtomDirection.South,
        AtomDirection.North,
        AtomDirection.East,
        AtomDirection.West,
        AtomDirection.Southeast,
        AtomDirection.Southwest,
        AtomDirection.Northeast,
        AtomDirection.Northwest
    };

    private static readonly byte[] PngHeader = { 0x89, 0x50, 0x4E, 0x47, 0xD, 0xA, 0x1A, 0xA };

    public sealed class ParsedDMIDescription {
        public int Width, Height;

        //The first state added, or the null string state if it exists.
        private string? defaultState;
        public Dictionary<string, (ParsedDMIState? staticstate, ParsedDMIState? movingstate)> States = new();

        /// <summary>
        /// Gets the requested state, or the default if it doesn't exist
        /// </summary>
        /// <remarks>The default state could also not exist</remarks>
        /// <param name="stateName">The requested state's name</param>
        /// <param name="moving">The requested state's movement tag</param>
        /// <returns>The requested state, default state, or null</returns>
        public ParsedDMIState? GetStateOrDefault(string? stateName, bool moving) {
            if (string.IsNullOrEmpty(stateName)) stateName = "";

            if (!States.TryGetValue(stateName, out var state)) {
                //stateName may not be in States, additional check for default state.
                if (!States.TryGetValue(defaultState ?? string.Empty, out state)) {
                    return null;
                }
            }

            return moving ? state.movingstate : state.staticstate;
        }

        /// <summary>
        /// Construct a string describing this DMI description<br/>
        /// In the same format as the text found in .dmi files
        /// </summary>
        /// <returns>This ParsedDMIDescription represented as text</returns>
        public string ExportAsText() {
            StringBuilder text = new();

            text.AppendLine("# BEGIN DMI");

            // This could either end up compressed or decompressed depending on how large this text ends up being.
            // So go with version 3.0, BYOND doesn't seem to care either way
            text.AppendLine("version = 3.0");
            text.AppendLine($"\twidth = {Width}");
            text.AppendLine($"\theight = {Height}");

            foreach (var state in States.Values) {
                state.staticstate?.ExportAsText(text);
                state.staticstate?.ExportAsText(text);
            }

            text.Append("# END DMI");

            return text.ToString();
        }

        public static ParsedDMIDescription CreateSingleFrame(int width, int height) {
            var desc = new ParsedDMIDescription {
                Width = width,
                Height = height
            };

            var state = new ParsedDMIState(string.Empty);
            var frame = new ParsedDMIFrame {
                X = 0,
                Y = 0,
                Delay = TimeSpan.FromMilliseconds(100)
            };

            state.Directions.Add(AtomDirection.South, new [] { frame });
            desc.States.Add(state.Name, (state, null));
            return desc;
        }

        /// <summary>
        /// Create DMI information that splits a larger image into icon states with the names "x,y"<br/>
        /// https://www.byond.com/docs/ref/info.html#/{notes}/tiled-icons
        /// </summary>
        /// <param name="width">Width of the whole image</param>
        /// <param name="height">Height of the whole image</param>
        /// <param name="iconSize">The size to split each icon state into</param>
        public static ParsedDMIDescription CreateSplitStates(uint width, uint height, int iconSize) {
            var desc = new ParsedDMIDescription {
                Width = iconSize,
                Height = iconSize
            };

            var xCount = (int)Math.Max(Math.Ceiling((float)width / iconSize), 1);
            var yCount = (int)Math.Max(Math.Ceiling((float)height / iconSize), 1);
            for (int x = 0; x < xCount; x++) {
                for (int y = 0; y < yCount; y++) {
                    var state = new ParsedDMIState($"{x},{y}");
                    var frame = new ParsedDMIFrame {
                        X = x * iconSize,
                        Y = (yCount * iconSize) - (y + 1) * iconSize, // "0,0" starts from the bottom left
                        Delay = TimeSpan.FromMilliseconds(100)
                    };

                    state.Directions.Add(AtomDirection.South, new [] { frame });
                    //TODO moving state?
                    desc.States.Add(state.Name, (state, null));
                }
            }

            return desc;
        }
    }

    public sealed class ParsedDMIState(string name) {
        public string Name = name;
        public bool Loop = true;
        public bool Rewind;
        public bool Movement;

        // TODO: This can only contain either 1, 4, or 8 directions. Enforcing this could simplify some things.
        public readonly Dictionary<AtomDirection, ParsedDMIFrame[]> Directions = new();

        /// <summary>
        /// The amount of animation frames this state has
        /// </summary>
        int FrameCount => (Directions.Count == 0) ? 0 : Directions.Values.First().Length;

        public ParsedDMIFrame[] GetFrames(AtomDirection direction = AtomDirection.South) {
            // Find another direction to use if this one doesn't exist
            if (!Directions.ContainsKey(direction)) {
                // The diagonal directions attempt to use east/west
                if (direction is AtomDirection.Northeast or AtomDirection.Southeast)
                    direction = AtomDirection.East;
                else if (direction is AtomDirection.Northwest or AtomDirection.Southwest)
                    direction = AtomDirection.West;

                // Use the south direction if the above still isn't valid
                if (!Directions.ContainsKey(direction))
                    direction = AtomDirection.South;
            }

            return Directions[direction];
        }

        public void ExportAsText(StringBuilder text) {
            text.AppendLine($"state = \"{Name}\"");
            text.AppendLine($"\tdirs = {GetExportedDirectionCount(Directions)}");
            text.AppendLine($"\tframes = {FrameCount}");

            if (Directions.Count > 0) {
                text.Append("\tdelay = ");
                var frames = Directions.Values.First(); // Delays should be the same in each direction
                for (var i = 0; i < frames.Length; i++) {
                    var delay = frames[i].Delay.TotalMilliseconds / 100; // Convert back to deciseconds

                    text.Append(delay.ToString(CultureInfo.InvariantCulture));
                    if (i != frames.Length - 1)
                        text.Append(',');
                }

                text.AppendLine();
            }

            if (!Loop) text.AppendLine("\tloop = 0");

            if (Rewind) text.AppendLine("\trewind = 1");

            if (Movement) text.AppendLine("\trewind = 1");
        }

        /// <summary>
        /// Get this state's frames
        /// </summary>
        /// <param name="dir">Which direction to get. Every direction if null.</param>
        /// <param name="frame">Which frame to get. Every frame if null.</param>
        /// <param name="asSouth">If dir isn't null, return the frames as facing south</param>
        /// <remarks>Invalid dir/frame args will give empty arrays</remarks>
        /// <returns>A dictionary containing the specified frames for each specified direction</returns>
        public Dictionary<AtomDirection, ParsedDMIFrame[]> GetFrames(AtomDirection? dir = null, int? frame = null, bool asSouth = false) {
            Dictionary<AtomDirection, ParsedDMIFrame[]> directions;
            if (dir == null) { // Get every direction
                directions = new(Directions);
            } else {
                directions = new(1);

                if (!Directions.TryGetValue(dir.Value, out var frames))
                    frames = Array.Empty<ParsedDMIFrame>();

                directions.Add(asSouth ? AtomDirection.South : dir.Value, frames);
            }

            if (frame != null) { // Only get a specified frame
                foreach (var direction in directions) {
                    if (direction.Value.Length > frame.Value) {
                        directions[direction.Key] = new[] { direction.Value[frame.Value] };
                    } else {
                        // Frame doesn't exist
                        directions[direction.Key] = Array.Empty<ParsedDMIFrame>();
                    }
                }
            }

            return directions;
        }
    }

    public sealed class ParsedDMIFrame {
        public int X, Y;
        public TimeSpan Delay;
    }

    /// <summary>
    /// The total directions present in an exported DMI.<br/>
    /// An icon state in a DMI must contain either 1, 4, or 8 directions.
    /// </summary>
    public static int GetExportedDirectionCount<T>(Dictionary<AtomDirection, T> directions) {
        // If we have any of these directions then we export 8 directions
        if (directions.ContainsKey(AtomDirection.Northeast) || directions.ContainsKey(AtomDirection.Southeast) ||
            directions.ContainsKey(AtomDirection.Southwest) || directions.ContainsKey(AtomDirection.Northwest)) {
            return 8;
        }

        // Any of these (without the above) means 4 directions
        if (directions.ContainsKey(AtomDirection.North) || directions.ContainsKey(AtomDirection.East) ||
            directions.ContainsKey(AtomDirection.West)) {
            return 4;
        }

        // Otherwise, 1 direction (just south)
        return 1;
    }

    public static ParsedDMIDescription ParseDMI(Stream stream) {
        if (VerifyBmp(stream)) {
            return ParseDMIBmp(stream);
        } else if (VerifyPng(stream)) {
            return ParseDMIPng(stream);
        } else {
            throw new Exception("Provided stream was not a valid image format (invalid magic bytes)");
        }
    }

    private static ParsedDMIDescription ParseDMIBmp(Stream stream) {
        stream.Seek(14, SeekOrigin.Begin);
        var reader = new BinaryReader(stream);
        var headerSize = reader.ReadUInt32();
        uint width, height;
        switch (headerSize)
        {
            case 12: // Old DIB header
                width = reader.ReadUInt16();
                height = reader.ReadUInt16();
                break;
            case 40: // New DIB header
                width = reader.ReadUInt32();
                height = reader.ReadUInt32();
                break;
            default:
                throw new Exception($"Unrecognized BMP header (size {headerSize})");
        }

        // TODO: Use CreateSplitStates if world.map_format == TILED_ICON_MAP
        return ParsedDMIDescription.CreateSingleFrame((int)width, (int)height);
    }

    private static ParsedDMIDescription ParseDMIPng(Stream stream) {
        var reader = new BinaryReader(stream);
        Vector2u? imageSize = null;

        while (stream.Position < stream.Length) {
            uint chunkLength = ReadBigEndianUint32(reader);
            string chunkType = Encoding.UTF8.GetString(reader.ReadBytes(4));
            long chunkDataPosition = stream.Position;

            switch (chunkType) {
                case "IHDR": //Image header, contains the image size
                    imageSize = new Vector2u(ReadBigEndianUint32(reader), ReadBigEndianUint32(reader));
                    stream.Seek(chunkLength - 4, SeekOrigin.Current); //Skip the rest of the chunk
                    break;
                case "zTXt": //Compressed text, likely contains our DMI description
                case "tEXt": //Uncompressed text. Not typical, but also works.
                    if (imageSize == null) throw new Exception("The PNG did not contain an IHDR chunk");

                    StringBuilder keyword = new StringBuilder();
                    while (reader.PeekChar() != 0 && keyword.Length < 79) {
                        keyword.Append(reader.ReadChar());
                    }

                    stream.Seek(1, SeekOrigin.Current); //Skip over null-terminator
                    if (chunkType == "zTXt")
                        stream.Seek(1, SeekOrigin.Current); //Skip over compression type

                    if (keyword.ToString() == "Description") {
                        byte[] uncompressedData;

                        if (chunkType == "zTXt") {
                            stream.Seek(2, SeekOrigin.Current); //Skip the first 2 bytes in the zlib format

                            DeflateStream deflateStream = new DeflateStream(stream, CompressionMode.Decompress);
                            MemoryStream uncompressedDataStream = new MemoryStream();

                            deflateStream.CopyTo(uncompressedDataStream, (int)chunkLength - keyword.Length - 2);

                            uncompressedData = new byte[uncompressedDataStream.Length];
                            uncompressedDataStream.Seek(0, SeekOrigin.Begin);
                            uncompressedDataStream.ReadExactly(uncompressedData);
                        } else {
                            //The text is not compressed so nothing fancy is required
                            uncompressedData = reader.ReadBytes((int) chunkLength - keyword.Length - 1);
                        }

                        string dmiDescription = Encoding.UTF8.GetString(uncompressedData, 0, uncompressedData.Length);
                        return ParseDMIDescription(dmiDescription, imageSize.Value.X);
                    }

                    // Wasn't the description chunk we were looking for
                    stream.Position = chunkDataPosition + chunkLength + 4;
                    break;
                default: //Nothing we care about, skip it
                    stream.Seek(chunkLength + 4, SeekOrigin.Current);
                    break;
            }
        }

        if (imageSize != null) {
            // No DMI description found, but we do have an image header
            // So treat this PNG as a single icon frame spanning the whole image
            return ParsedDMIDescription.CreateSingleFrame((int)imageSize.Value.X, (int)imageSize.Value.Y);
        }

        throw new Exception("PNG is missing an image header");
    }

    private static ParsedDMIDescription ParseDMIDescription(string dmiDescription, uint imageWidth) {
        ParsedDMIDescription description = new ParsedDMIDescription();
        ParsedDMIState? currentState = null;
        string currentStateName = "";
        int currentFrameX = 0;
        int currentFrameY = 0;
        int currentStateDirectionCount = 1;
        int currentStateFrameCount = 1;
        float[] currentStateFrameDelays = Array.Empty<float>();

        Span<Range> lines = new Span<Range>();
        var rawLines = dmiDescription.AsSpan().Split(lines, '\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var chunk in lines) {
            var line = dmiDescription.AsSpan(chunk);

            if (line.StartsWith('#')) continue;

            int equalsIndex = line.IndexOf('=');

            if (equalsIndex != -1) {
                var key = line[..(equalsIndex - 1)].TrimEnd();
                var value = line[(equalsIndex + 1)..].TrimStart();

                switch (key) {
                    case "version":
                        // No need to care about this at the moment
                        break;

                    case "width":
                        description.Width = int.Parse(value);
                        break;

                    case "height":
                        description.Height = int.Parse(value);
                        break;

                    case "state":
                        if (currentState != null) { //Do final processing on existing state.
                            LocalFnProcessState();
                        }

                        currentStateName = ParseString(value.ToString());
                        currentStateFrameCount = 1;
                        currentStateFrameDelays = [];
                        currentState = new ParsedDMIState(currentStateName);
                        break;

                    case "dirs":
                        currentStateDirectionCount = int.Parse(value);
                        break;

                    case "frames":
                        currentStateFrameCount = int.Parse(value);
                        break;

                    case "delay":
                        var frameDelayHolder = new List<float>((value.Length / 2) + 1); //Hopeful guess
                        foreach (var delayRange in value.Split(',')) {
                            frameDelayHolder.Add(float.Parse(value[delayRange], CultureInfo.InvariantCulture));
                        }

                        currentStateFrameDelays = frameDelayHolder.ToArray();
                        break;

                    case "loop":
                        if (currentState is not null)
                            currentState.Loop = (int.Parse(value) == 0);
                        break;

                    case "rewind":
                        if (currentState is not null)
                            currentState.Rewind = (int.Parse(value) == 1);
                        break;

                    case "movement":
                        //TODO CAT: implement this empty block stub
                        if (currentState is not null)
                            currentState.Movement = (int.Parse(value) == 1);
                        break;

                    case "hotspot":
                        //TODO
                        break;

                    default:
                        throw new Exception($"Invalid key \"{key}\" in DMI description");
                }
            } else {
                throw new Exception($"Invalid line in DMI description: \"{line}\"");
            }
        }

        //Push the last state out of queue
        if (currentState is not null) LocalFnProcessState();

        return description;

        //Local (Internal) Function for processing the queued current state into its final product and adding it to ParsedDMIDescription.
        void LocalFnProcessState() {
            //Precalculate frame delays
            TimeSpan[] timespanDelays;
            if (currentStateFrameDelays is []) { //Default empty to values of 1 decisecond (100 milli)
                timespanDelays = new TimeSpan[currentStateFrameCount];
                Array.Fill(timespanDelays, TimeSpan.FromMilliseconds(100));
            } else { //Convert values from deciseconds to milliseconds
                timespanDelays = Array.ConvertAll(currentStateFrameDelays,
                    delay => TimeSpan.FromMilliseconds(delay * 100));
            }

            //Prefill Directions dictionary with the needed DMIFrame arrays.
            for (var i = 0; i < currentStateDirectionCount; i++) {
                currentState.Directions[DMIFrameDirections[i]] = new ParsedDMIFrame[currentStateFrameCount];
            }

            //For each frame, fill every direction with spritesheet location and delay.
            for (var f = 0; f < currentStateFrameCount; f++) {
                var fDelay = timespanDelays[f]; //Cache frame delay
                foreach(var d in DMIFrameDirections[..currentStateDirectionCount]) {
                    currentState.Directions[d][f] = new ParsedDMIFrame {
                        X = currentFrameX,
                        Y = currentFrameY,
                        Delay = fDelay
                    };

                    currentFrameX += description.Width;
                    //Move to the next row on the spritesheet if we exceed its width
                    if (currentFrameX < imageWidth) continue;
                    currentFrameX = 0;
                    currentFrameY += description.Height;
                }
            }

            if (description.States.TryGetValue(currentStateName, out var existingState)) { //State already exists, reuse tuple.
                switch (currentState.Movement) {
                    case true:
                        if (existingState.movingstate == null) {
                            description.States[currentStateName] = (existingState.staticstate, currentState);
                        }

                        break;

                    case false:
                        if (existingState.staticstate == null) {
                            description.States[currentStateName] = (currentState, existingState.staticstate);
                        }

                        break;
                }
            } else { //State does not exist, make a new tuple.
                description.States.TryAdd(currentStateName, (currentState.Movement ? null : currentState, currentState.Movement ? currentState : null));
            }
        }
    }

    private static string ParseString(string value) {
        if (value.StartsWith('"') && value.EndsWith('"')) {
            return value.Substring(1, value.Length - 2);
        } else {
            throw new Exception($"Invalid string in DMI description: {value}");
        }
    }

    private static bool VerifyPng(Stream stream) {
        stream.Seek(0, SeekOrigin.Begin);
        return PngHeader.All(t => (stream.ReadByte() == t));
    }

    private static bool VerifyBmp(Stream stream) {
        stream.Seek(0, SeekOrigin.Begin);
        return stream.ReadByte() == 0x42 && stream.ReadByte() == 0x4D;
    }

    private static uint ReadBigEndianUint32(BinaryReader reader) {
        var bytes = reader.ReadBytes(4);
        Array.Reverse(bytes); //Little to Big-Endian
        return BitConverter.ToUInt32(bytes);
    }
}
