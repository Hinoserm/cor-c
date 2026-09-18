namespace Corsac.Lang.Lto;

public sealed record BackendRequest(string Input, string Output, IReadOnlyList<IrImport> Imports, IReadOnlySet<string>? Retained = null);
