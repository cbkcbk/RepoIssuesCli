namespace RepoDocsCli.Features.Docs;

public static class FeatureStatusNames
{
    public static string Name(this FeatureStatus status) => status switch
    {
        FeatureStatus.Proposed => "proposed",
        FeatureStatus.Planned => "planned",
        FeatureStatus.InProgress => "in-progress",
        FeatureStatus.Shipped => "shipped",
        FeatureStatus.Deprecated => "deprecated",
        _ => status.ToString(),
    };
}
