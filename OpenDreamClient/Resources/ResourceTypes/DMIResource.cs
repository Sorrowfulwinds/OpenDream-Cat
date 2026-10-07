using System.IO;
using OpenDreamShared.Dream;
using OpenDreamShared.Resources;
using Robust.Client.Graphics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace OpenDreamClient.Resources.ResourceTypes;

public sealed class DMIResource : DreamResource {
    private readonly Dictionary<string, State> _states;
    public DMIParser.ParsedDMIDescription Description;
    public Vector2i IconSize;
    public Texture Texture;

    public DMIResource(int id, byte[] data) : base(id, data) {
        _states = new Dictionary<string, State>();
        ProcessDMIData();
    }

    public override void UpdateData(byte[] data) {
        base.UpdateData(data);
        ProcessDMIData();
    }

    private void ProcessDMIData() {
        using Stream dmiStream = new MemoryStream(Data);
        DMIParser.ParsedDMIDescription description = DMIParser.ParseDMI(dmiStream);

        dmiStream.Seek(0, SeekOrigin.Begin);

        Image<Rgba32> image = Image.Load<Rgba32>(dmiStream);
        Texture = IoCManager.Resolve<IClyde>().LoadTextureFromImage(image, $"DMI Resource #{Id}");
        IconSize = new Vector2i(description.Width, description.Height);
        Description = description;

        _states.Clear();
        foreach (DMIParser.ParsedDMIState parsedState in description.States.Values) {
            var state = new State(Texture, parsedState, description.Width, description.Height);

            _states.Add(parsedState.Name, state);
        }
    }

    public State? GetState(string? stateName) {
        if (stateName == null || !_states.ContainsKey(stateName))
            return _states.TryGetValue(string.Empty, out State state) ? state : null; // Default state, if one exists

        return _states[stateName];
    }

    public ICursor? GetStateAsImage(IClyde clyde, string? stateName) {
        using var dmiStream = new MemoryStream(Data);
        DMIParser.ParsedDMIDescription description = DMIParser.ParseDMI(dmiStream);

        dmiStream.Seek(0, SeekOrigin.Begin);

        Image<Rgba32> image = Image.Load<Rgba32>(dmiStream);
        DMIParser.ParsedDMIState? state = description.GetStateOrDefault(stateName);
        if (!(state?.Directions.TryGetValue(AtomDirection.South, out DMIParser.ParsedDMIFrame[]? frames) ?? false))
            return null;

        Image<Rgba32> stateImage = image.Clone(clone => {
            DMIParser.ParsedDMIFrame frame = frames[0];

            clone.Crop(new Rectangle(frame.X, frame.Y, frame.X + description.Width, frame.Y + description.Height));
        });

        Vector2i hotspot = state.Hotspot ?? (0, stateImage.Height - 1); // Default to the top-left
        ICursor cursor = clyde.CreateCursor(stateImage, hotspot);
        return cursor;
    }

    public struct State {
        public Dictionary<AtomDirection, AtlasTexture[]> Frames;

        public State(Texture texture, DMIParser.ParsedDMIState parsedState, int width, int height) {
            Frames = new Dictionary<AtomDirection, AtlasTexture[]>();

            foreach ((AtomDirection dir, DMIParser.ParsedDMIFrame[] parsedFrames) in parsedState.Directions) {
                var frames = new AtlasTexture[parsedFrames.Length];

                for (var i = 0; i < parsedFrames.Length; i++) {
                    DMIParser.ParsedDMIFrame parsedFrame = parsedFrames[i];

                    frames[i] = new AtlasTexture(texture,
                        new UIBox2(parsedFrame.X, parsedFrame.Y, parsedFrame.X + width, parsedFrame.Y + height));
                }

                Frames.Add(dir, frames);
            }
        }

        public AtlasTexture[] GetFrames(AtomDirection direction) {
            // Find another direction to use if this one doesn't exist
            if (!Frames.ContainsKey(direction)) {
                switch (direction)
                {
                    // The diagonal directions attempt to use east/west
                    case AtomDirection.Northeast or AtomDirection.Southeast:
                        direction = AtomDirection.East;
                        break;
                    case AtomDirection.Northwest or AtomDirection.Southwest:
                        direction = AtomDirection.West;
                        break;
                }

                // Use the south direction if the above still isn't valid
                if (!Frames.ContainsKey(direction))
                    direction = AtomDirection.South;
            }

            return Frames[direction];
        }
    }
}
