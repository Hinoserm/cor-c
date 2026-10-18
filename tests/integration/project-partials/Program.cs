namespace PartialsFixture;

public static class Program
{
    public static int Main()
    {
        Grow g = new Grow();
        System.Console.WriteLine(Grow.Wider(3) + " " + Grow.Limit + " " + Grow.Twice(4) + " " + g.Name() + " " + new Clock().Ticks + " " + Os.Name);
        return Grow.Wider(41);
    }
}
