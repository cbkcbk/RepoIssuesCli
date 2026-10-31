using RepoInfraCli.Features.Infra.Apps;
using RepoInfraCli.Features.Infra.Evaluation.Natives;
using RepoInfraCli.Features.Infra.Graph;

namespace RepoInfraCli.Features.Infra.Validation;

public static class BuildValidator
{
    public static async Task<IReadOnlyList<InfraDiagnostic>> ValidateAsync(InfraGraph graph, InfraWorkspace workspace, IBuildTools tools, CancellationToken cancellationToken = default)
    {
        var diagnostics = new List<InfraDiagnostic>();
        foreach (var build in graph.Apps.Select(app => app.Build).DistinctBy(build => build.Id).Where(build => build.Kind == DotnetModule.ContainerKind))
        {
            var project = ContainerSettings.ProjectPath(workspace, build);
            if (!File.Exists(project))
                continue;

            ContainerProperties properties;
            try
            {
                properties = await tools.ReadPropertiesAsync(project, cancellationToken);
            }
            catch (BuildToolException exception)
            {
                diagnostics.Add(InfraDiagnostic.Warning(DiagnosticCodes.ContainerUnreadable, $"dotnet.container could not read the container settings of {build.Options.GetString("project")}: {exception.Message}", build.Location));
                continue;
            }

            diagnostics.AddRange(ContainerSettings.Resolve(build, properties).Conflicts
                .Select(conflict => InfraDiagnostic.Error(DiagnosticCodes.ContainerConflict, $"dotnet.container: {conflict}", build.Location)));
        }
        return diagnostics;
    }
}
