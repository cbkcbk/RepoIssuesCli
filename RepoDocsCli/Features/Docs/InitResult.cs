namespace RepoDocsCli.Features.Docs;

public sealed record InitResult(string Folder, bool Created, IReadOnlyList<string> Files, IReadOnlyList<Feature> Features);
