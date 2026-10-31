namespace RepoInfraCli.Features.Infra.Postgres;

public sealed class CtrlC : IDisposable
{
    public const string Message = "cancelled with Ctrl+C; the copy containers and the temporary database were removed and the target is untouched";

    readonly CancellationTokenSource source;

    public CtrlC(CancellationToken outer)
    {
        source = CancellationTokenSource.CreateLinkedTokenSource(outer);
        Console.CancelKeyPress += Cancel;
    }

    public CancellationToken Token => source.Token;

    void Cancel(object? sender, ConsoleCancelEventArgs args)
    {
        args.Cancel = true;
        source.Cancel();
    }

    public void Dispose()
    {
        Console.CancelKeyPress -= Cancel;
        source.Dispose();
    }
}
