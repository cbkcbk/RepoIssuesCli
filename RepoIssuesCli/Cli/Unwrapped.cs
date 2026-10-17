using Spectre.Console;
using Spectre.Console.Rendering;

namespace RepoIssuesCli.Cli;

public sealed class Unwrapped(IRenderable content) : IRenderable
{
    public Measurement Measure(RenderOptions options, int maxWidth) => content.Measure(options, int.MaxValue);

    public IEnumerable<Segment> Render(RenderOptions options, int maxWidth) => content.Render(options, int.MaxValue);
}
