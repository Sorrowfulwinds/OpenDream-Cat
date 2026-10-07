using System.Collections.Generic;
using Lidgren.Network;
using Robust.Shared.Network;
using Robust.Shared.Serialization;

namespace OpenDreamShared.Network.Messages;

public sealed class MsgSoundQueryResponse : NetMessage {
    public int PromptId;
    public List<SoundData> Sounds = default!;
    public override MsgGroups MsgGroup => MsgGroups.EntityEvent;

    public override void ReadFromBuffer(NetIncomingMessage buffer, IRobustSerializer serializer) {
        PromptId = buffer.ReadVariableInt32();
        ushort soundCount = buffer.ReadUInt16();

        Sounds = new List<SoundData>(soundCount);

        if (soundCount == 0) return;

        for (var i = 0; i < soundCount; i++) Sounds.Add(new SoundData(buffer));
    }

    public override void WriteToBuffer(NetOutgoingMessage buffer, IRobustSerializer serializer) {
        buffer.WriteVariableInt32(PromptId);

        int soundCount = Sounds.Count;
        buffer.Write((ushort)soundCount);

        for (var i = 0; i < soundCount; i++) Sounds[i].WriteToBuffer(buffer);
    }
}
