namespace Corsac.Lang;

/// <summary>Read immutable cached tokens directly; copy only when generic parsing splits a token.</summary>
public sealed class ParserTokens
{
    private readonly Token[]? snapshot;
    private List<Token>? mutable;
    public ParserTokens(List<Token> tokens) { mutable = tokens; }
    public ParserTokens(Token[] tokens) { snapshot = tokens; }
    public int Count => mutable is null ? snapshot!.Length : mutable.Count;
    public Token this[int index]
    {
        get => mutable is null ? snapshot![index] : mutable[index];
        set => Writable()[index] = value;
    }
    private List<Token> Writable() => mutable ??= new List<Token>(snapshot!);
    public void Insert(int index, Token token) => Writable().Insert(index, token);
    public void RemoveAt(int index) => Writable().RemoveAt(index);
}
