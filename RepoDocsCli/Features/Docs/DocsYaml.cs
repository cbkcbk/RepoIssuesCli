using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace RepoDocsCli.Features.Docs;

public static class DocsYaml
{
    static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithEnumNamingConvention(HyphenatedNamingConvention.Instance)
        .WithTypeConverter(new TimestampYamlConverter())
        .WithTypeConverter(new DateYamlConverter())
        .WithTypeConverter(new AnchorYamlConverter())
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .WithEventEmitter(next => new NumberListEmitter(next))
        .WithIndentedSequences()
        .DisableAliases()
        .Build();

    static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithEnumNamingConvention(HyphenatedNamingConvention.Instance)
        .WithTypeConverter(new TimestampYamlConverter())
        .WithTypeConverter(new DateYamlConverter())
        .WithTypeConverter(new AnchorYamlConverter())
        .Build();

    public static string Serialize<T>(T value) => Serializer.Serialize(value).ReplaceLineEndings("\n");

    public static T? Deserialize<T>(string yaml) => Deserializer.Deserialize<T?>(yaml);
}
