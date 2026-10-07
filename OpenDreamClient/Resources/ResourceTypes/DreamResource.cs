using System.IO;
using JetBrains.Annotations;

namespace OpenDreamClient.Resources.ResourceTypes;

[Virtual]
public class DreamResource {
    public readonly int Id;

    protected byte[] Data;
    public List<Action> OnUpdateCallbacks = new();

    [UsedImplicitly]
    public DreamResource(int id, byte[] data) {
        Id = id;
        Data = data;
    }

    public void WriteTo(Stream stream) {
        stream.Write(Data);
    }

    public virtual void UpdateData(byte[] data) {
        Data = data;
    }
}
