namespace SlotsFixture;

// A public record class -- its Equals(Mark?) is a virtual of its own -- and a
// class with a virtual method, both used from every unit.
public sealed record Mark(int Kind, string Text);

public class Counter
{
    public virtual int Step(int value) => value + 1;
}

public sealed class Doubler : Counter
{
    public override int Step(int value) => value * 2;
}
