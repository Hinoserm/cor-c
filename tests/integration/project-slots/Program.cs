namespace SlotsFixture;

public static class Program
{
    public static int Main()
    {
        Mark mark = new Mark(20, "twenty");
        // 20 * 2 by Doubler's override, plus 1 for equal marks.
        int scanned = Scanner.Scan(new Doubler(), mark);
        return mark.Equals(new Mark(20, "twenty")) && !mark.Equals(new Mark(21, "twenty")) ? scanned : 1;
    }
}
