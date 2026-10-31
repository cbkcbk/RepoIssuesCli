using System.Security.Cryptography;
using System.Text;
using RepoInfraCli.Features.Infra.Graph;

namespace RepoInfraCli.Features.Infra.Hosts.Tasks;

public sealed class FileTask : IHostTask
{
    public string Kind => TaskKinds.File;

    public string Label(TaskNode task) => $"file {task.Name}";

    sealed record State(byte[]? Content, bool Upload);

    public async Task<TaskProbe> ProbeAsync(TaskContext context, CancellationToken cancellationToken)
    {
        var path = Sh.Quote(context.Name);
        var content = DesiredContent(context);
        var mode = DesiredMode(context);
        var owner = context.Options.GetString("owner");

        var script = $"""
            if [ -e {path} ]; then
              echo "mode $(stat -c '%a' {path})"
              echo "owner $(stat -c '%U:%G' {path})"
              echo "sha256 $(sha256sum {path} | cut -d' ' -f1)"
            fi
            """;
        var result = await context.Connection.RunCheckedAsync($"file {context.Name} probe", script, cancellationToken);
        var facts = Sh.Lines(result.Stdout).ToDictionary(line => line.Key, line => line.Value);

        if (!facts.ContainsKey("mode"))
        {
            if (content is null)
                throw new TaskPlanException($"file {context.Name} does not exist and has no content or source");
            var details = new List<string> { $"{content.Length} bytes" };
            if (mode is not null)
                details.Add($"mode {mode}");
            if (owner is not null)
                details.Add($"owner {owner}");
            return new TaskProbe([Change.Add($"create ({string.Join(", ", details)})")], new State(content, true));
        }

        var changes = new List<Change>();
        var currentHash = facts.GetValueOrDefault("sha256", "");
        var contentChanged = content is not null && Sha256(content) != currentHash;
        if (contentChanged)
            changes.Add(Change.Update($"content (sha256 {Short(currentHash)} -> {Short(Sha256(content!))})"));
        if (mode is not null && Octal(facts["mode"]) != Octal(mode))
            changes.Add(Change.Update($"mode {facts["mode"]} -> {mode}"));
        if (owner is not null && !OwnerMatches(facts.GetValueOrDefault("owner", ""), owner))
            changes.Add(Change.Update($"owner {facts.GetValueOrDefault("owner")} -> {owner}"));
        return new TaskProbe(changes, new State(content, contentChanged));
    }

    public async Task ApplyAsync(TaskContext context, TaskProbe probe, CancellationToken cancellationToken)
    {
        var state = (State)probe.State!;
        var mode = DesiredMode(context);
        var owner = context.Options.GetString("owner");
        if (state.Upload)
        {
            await context.Connection.UploadAsync(context.Name, state.Content, mode, owner, cancellationToken);
            return;
        }

        var script = new StringBuilder().AppendLine("set -eu");
        if (mode is not null)
            script.AppendLine($"chmod {Sh.Quote(mode)} {Sh.Quote(context.Name)}");
        if (owner is not null)
            script.AppendLine($"chown {Sh.Quote(owner)} {Sh.Quote(context.Name)}");
        await context.Connection.RunCheckedAsync($"file {context.Name}", script.ToString(), cancellationToken);
    }

    static byte[]? DesiredContent(TaskContext context) => context.Options["source"] switch
    {
        FileRef file when File.Exists(file.Resolved) => File.ReadAllBytes(file.Resolved),
        FileRef file => throw new TaskPlanException($"source {file.Path} of file {context.Name} does not exist ({file.Resolved})"),
        _ => context.Options["content"] is { } content ? Encoding.UTF8.GetBytes(TaskOptions.Text(content)) : null,
    };

    static string? DesiredMode(TaskContext context)
    {
        var mode = context.Options.GetString("mode");
        if (mode is not null && (mode.Length is < 3 or > 4 || !mode.All(digit => digit is >= '0' and <= '7')))
            throw new TaskPlanException($"mode '{mode}' of file {context.Name} must be octal, as in \"0644\"");
        return mode;
    }

    static int Octal(string mode) => Convert.ToInt32(mode, 8);

    static bool OwnerMatches(string current, string desired)
    {
        var (currentUser, currentGroup) = SplitOwner(current);
        var (desiredUser, desiredGroup) = SplitOwner(desired);
        return currentUser == desiredUser && (desiredGroup is null || currentGroup == desiredGroup);
    }

    static (string User, string? Group) SplitOwner(string owner) =>
        owner.IndexOf(':') is var colon and >= 0 ? (owner[..colon], owner[(colon + 1)..]) : (owner, null);

    public static string Sha256(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

    static string Short(string hash) => hash.Length > 12 ? hash[..12] : hash.Length == 0 ? "?" : hash;
}
