using RepoInfraCli.Features.Infra.Graph;

namespace RepoInfraCli.Features.Infra.Hosts.Tasks;

public static class TaskOptions
{
    public static IReadOnlyList<string> Strings(this MapValue options, string key) =>
        options[key] is ListValue list ? [.. list.Items.Select(Text)] : [];

    public static bool? Flag(this MapValue options, string key) => options[key] is BoolValue flag ? flag.Value : null;

    public static string Text(InfraValue value) => value switch
    {
        StringValue text => text.Value,
        OutputRef or SecretRef => throw new TaskPlanException($"a value is {value.Display()}; host tasks need values known at plan time"),
        _ => value.Display(),
    };
}
