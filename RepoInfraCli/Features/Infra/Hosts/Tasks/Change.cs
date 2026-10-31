namespace RepoInfraCli.Features.Infra.Hosts.Tasks;

public enum ChangeMarker
{
    Add,
    Update,
    Remove,
    Replace,
    Run,
    Unsupported,
}

public sealed record Change(ChangeMarker Marker, string Text)
{
    public static Change Add(string text) => new(ChangeMarker.Add, text);

    public static Change Update(string text) => new(ChangeMarker.Update, text);

    public static Change Remove(string text) => new(ChangeMarker.Remove, text);

    public static Change Run(string text) => new(ChangeMarker.Run, text);

    public static Change Unsupported(string text) => new(ChangeMarker.Unsupported, text);
}

public static class ChangeMarkers
{
    public static string Symbol(this ChangeMarker marker) => marker switch
    {
        ChangeMarker.Add => "+",
        ChangeMarker.Update => "~",
        ChangeMarker.Remove => "-",
        ChangeMarker.Replace => "-/+",
        ChangeMarker.Run => "!",
        _ => "?",
    };

    public static string Color(this ChangeMarker marker) => marker switch
    {
        ChangeMarker.Add => "green",
        ChangeMarker.Update => "yellow",
        ChangeMarker.Remove or ChangeMarker.Replace => "red",
        ChangeMarker.Run => "blue",
        _ => "grey",
    };

    public static bool IsDrift(this ChangeMarker marker) => marker is ChangeMarker.Add or ChangeMarker.Update or ChangeMarker.Remove or ChangeMarker.Replace;

    public static bool IsActionable(this ChangeMarker marker) => marker.IsDrift() || marker == ChangeMarker.Run;
}
