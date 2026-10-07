using Lidgren.Network;
using Robust.Shared.Network;
using Robust.Shared.Serialization;

namespace OpenDreamShared.Network.Messages;

public sealed class MsgBrowseResource : NetMessage {
    public byte[] DataHash = [];

    public string Filename = string.Empty;

    // TODO: Browse should be on its own channel or something.
    public override MsgGroups MsgGroup => MsgGroups.EntityEvent;

    public override void ReadFromBuffer(NetIncomingMessage buffer, IRobustSerializer serializer) {
        Filename = buffer.ReadString();
        DataHash = buffer.ReadBytes(32);
    }

    public override void WriteToBuffer(NetOutgoingMessage buffer, IRobustSerializer serializer) {
        buffer.Write(Filename);
        buffer.Write(DataHash);
    }
}
