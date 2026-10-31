using System.Diagnostics;
using System.Text;

namespace RepoInfraCli.Features.Infra.Apps;

public sealed record ProcessResult(int ExitCode, string Stdout, string Stderr)
{
    public bool Succeeded => ExitCode == 0;

    public string Tail(int characters = 1500)
    {
        var text = (Stderr.Trim().Length > 0 ? Stdout.Trim() + "\n" + Stderr.Trim() : Stdout.Trim()).Trim();
        return text.Length <= characters ? text : "..." + text[^characters..];
    }
}

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string? workingDirectory = null, string? stdin = null, CancellationToken cancellationToken = default);
}

public sealed class ProcessRunner : IProcessRunner
{
    public static ProcessRunner Instance { get; } = new();

    public async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string? workingDirectory = null, string? stdin = null, CancellationToken cancellationToken = default)
    {
        var start = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (workingDirectory is not null)
            start.WorkingDirectory = workingDirectory;
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"could not start {fileName}");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        if (stdin is not null)
        {
            await process.StandardInput.WriteAsync(stdin.AsMemory(), cancellationToken);
            process.StandardInput.Close();
        }

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
        return new ProcessResult(process.ExitCode, await stdout, await stderr);
    }
}
