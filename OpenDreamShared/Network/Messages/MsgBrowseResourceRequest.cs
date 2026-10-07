using Lidgren.Network;
using Robust.Shared.Network;
using Robust.Shared.Serialization;

namespace OpenDreamShared.Network.Messages;

public sealed class MsgBrowseResourceRequest : NetMessage {
    public string Filename = string.Empty;

    // TODO: Browse should be on its own channel or something.
    public override MsgGroups MsgGroup => MsgGroups.EntityEvent;

    public override void ReadFromBuffer(NetIncomingMessage buffer, IRobustSerializer serializer) {
        Filename = buffer.ReadString();
    }

    public override void WriteToBuffer(NetOutgoingMessage buffer, IRobustSerializer serializer) {
        buffer.Write(Filename);
    }
}
