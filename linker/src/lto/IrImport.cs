namespace Corsac.Lang.Lto;

public sealed record IrImport(string Symbol, byte[] Body, long DecodeBytes = 0);
