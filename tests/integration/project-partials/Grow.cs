namespace PartialsFixture;

public partial class Grow
{
    public int Size = 2;
    public static int Twice(int n) => Wider(n) * 2;
    public string Name() => "grow" + Limit + Tag();
}
