using OpenDreamClient.Resources;
using OpenDreamClient.Resources.ResourceTypes;
using OpenDreamShared.Network.Messages;
using Robust.Client.Audio;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Components;
using Robust.Shared.Network;

namespace OpenDreamClient.Audio;

public sealed partial class DreamSoundEngine : IDreamSoundEngine {
    private const int SoundChannelLimit = 1024;

    private readonly DreamSoundChannel?[] _channels = new DreamSoundChannel[SoundChannelLimit];
    [Dependency] private IAudioManager _audioManager = default!;
    private AudioSystem? _audioSystem;
    [Dependency] private IEntityManager _entityManager = default!;
    [Dependency] private IEntitySystemManager _entitySystemManager = default!;
    [Dependency] private ILogManager _logManager = default!;
    [Dependency] private INetManager _netManager = default!;

    [Dependency] private IDreamResourceManager _resourceManager = default!;

    private ISawmill _sawmill = default!;

    public void Initialize() {
        _sawmill = _logManager.GetSawmill("opendream.audio");

        _netManager.RegisterNetMessage<MsgSound>(RxSound);
        _netManager.RegisterNetMessage<MsgSoundQuery>(RxSoundQuery);
        _netManager.RegisterNetMessage<MsgSoundQueryResponse>();

        _netManager.Disconnect += DisconnectedFromServer;
    }

    public void PlaySound(SoundData soundData, MsgSound.FormatType format, ResourceSound sound) {
        if (_audioSystem == null)
            _entitySystemManager.Resolve(ref _audioSystem);

        var channel = (int)soundData.Channel;

        if (channel == 0) {
            //First available channel
            for (var i = 0; i < _channels.Length; i++)
                //if it's null, deleted, or queued for deletion, it's free
                if (_channels[i] == null || _entityManager.Deleted(_channels[i]!.Source.Entity) ||
                    _entityManager.IsQueuedForDeletion(_channels[i]!.Source.Entity)) {
                    channel = i + 1;
                    break;
                }

            if (channel == 0) {
                _sawmill.Error("Failed to find a free audio channel to play a sound on");
                return;
            }
        }

        StopChannel(channel);

        AudioStream? stream = sound.GetStream(format, _audioManager);
        if (stream == null) {
            _sawmill.Error($"Failed to load audio ${sound}");
            return;
        }

        float db = 20 * MathF.Log10(soundData.Volume / 100.0f); // convert from DM volume (0-100) to OpenAL volume (db)
        (EntityUid Entity, AudioComponent Component)? source = _audioSystem.PlayGlobal(stream, null,
            AudioParams.Default.WithVolume(db).WithPlayOffset(soundData.Offset)
                .WithLoop(soundData.Repeat != 0)); // TODO: Positional audio.
        if (source == null) {
            _sawmill.Error($"Failed to play audio ${sound}");
            return;
        }

        _channels[channel - 1] = new DreamSoundChannel(_audioSystem, source.Value, soundData);
    }

    public void StopChannel(int channel) {
        ref DreamSoundChannel? ch = ref _channels[channel - 1];

        ch?.Stop();
        // This will null the corresponding index in the array.
        ch = null;
    }

    public void StopAllChannels() {
        for (var i = 0; i < SoundChannelLimit; i++) StopChannel(i + 1);
    }

    public List<SoundData> GetSoundQuery() {
        var result = new List<SoundData>();
        foreach (DreamSoundChannel? channel in _channels) {
            if (channel is null) continue;
            result.Add(channel.SoundData);
        }

        return result;
    }

    private void RxSound(MsgSound msg) {
        if (msg.ResourceId.HasValue)
            _resourceManager.LoadResourceAsync<ResourceSound>(msg.ResourceId.Value,
                sound => PlaySound(msg.SoundData, msg.Format!.Value, sound));
        else
            StopChannel(msg.SoundData.Channel);
    }

    private void RxSoundQuery(MsgSoundQuery soundQuery) {
        var response = new MsgSoundQueryResponse {
            PromptId = soundQuery.PromptId,
            Sounds = GetSoundQuery()
        };
        _netManager.ClientSendMessage(response);
    }

    private void DisconnectedFromServer(object? sender, NetDisconnectedArgs e) {
        StopAllChannels();
    }
}
