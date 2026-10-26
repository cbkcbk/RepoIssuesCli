namespace RepoDocsCli.Features.Docs;

public sealed partial class DocsStore
{
    /// <summary>Writes a mechanical hint fix as is: <c>created</c> and <c>updated</c> are not stamped.</summary>
    public void SaveHintFix(Feature feature) => Write(PathFor(FeatureId.Require(feature.Id)), DocsYaml.Serialize(feature));

    /// <inheritdoc cref="SaveHintFix(Feature)"/>
    public void SaveHintFix(RepoDocs repo) => Write(RepoFile, DocsYaml.Serialize(repo));
}
