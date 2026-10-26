using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.EventEmitters;

namespace RepoDocsCli.Features.Docs;

public sealed class NumberListEmitter(IEventEmitter next) : ChainedEventEmitter(next)
{
    public override void Emit(SequenceStartEventInfo eventInfo, IEmitter emitter)
    {
        if (eventInfo.Source.Type == typeof(List<int>))
            eventInfo.Style = SequenceStyle.Flow;

        base.Emit(eventInfo, emitter);
    }
}
