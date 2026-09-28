namespace SlotsFixture;

// A local function with a ref parameter, which Func and Action cannot say:
// the compiler makes a delegate interface of this unit's own for it.
public static class Scanner
{
    public static int Scan(Counter counter, Mark mark)
    {
        int total = 0;
        void Add(ref int into, int amount) { into += amount; }
        Add(ref total, counter.Step(mark.Kind));
        Action<int> twice = n => Add(ref total, n);
        twice(mark.Equals(new Mark(mark.Kind, mark.Text)) ? 1 : 100);
        return total;
    }
}
