using System.ComponentModel;
using RepoDocsCli.Features.Docs;
using Spectre.Console.Cli;

namespace RepoDocsCli.Cli.Commands;

[Description("Git merge driver for .repo-docs files: merges per entry, the later timestamp wins. Configure it as 'docs merge-driver %O %A %B %P'.")]
public sealed class MergeDriverCommand : Command<MergeDriverCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<ancestor>")]
        [Description("The common ancestor version (%O).")]
        public string Ancestor { get; init; } = "";

        [CommandArgument(1, "<current>")]
        [Description("Our version (%A); the merge result is written here.")]
        public string Current { get; init; } = "";

        [CommandArgument(2, "<other>")]
        [Description("Their version (%B).")]
        public string Other { get; init; } = "";

        [CommandArgument(3, "<path>")]
        [Description("The file's path in the repository (%P).")]
        public string RepoPath { get; init; } = "";
    }

    public override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var textual = Git.Run(Environment.CurrentDirectory, "merge-file", "-p", "--diff3", "-L", "ours", "-L", "base", "-L", "theirs",
            settings.Current, settings.Ancestor, settings.Other);
        if (textual is null || (!textual.Succeeded && !ConflictMarkers.Contains(textual.Output)))
            throw new InvalidOperationException($"git merge-file failed for {settings.RepoPath}.");

        var merged = textual.Succeeded ? textual.Output : DocsMerge.Resolve(settings.RepoPath, textual.Output);
        File.WriteAllText(settings.Current, merged ?? textual.Output);
        return merged is null ? ExitCodes.ValidationFailed : ExitCodes.Success;
    }
}
