#nullable enable
using System.Buffers.Binary;
using Corsac.Lang.Ir;

namespace Corsac.Lang.Elf;

/// <summary>
/// Thrown when a link fails. Every problem found is in <see cref="Errors"/>,
/// each naming the symbol or section and the object it came from, so one
/// run reports everything rather than the first thing.
/// </summary>
public sealed class LinkException : Exception
{
    public IReadOnlyList<string> Errors { get; }

    public LinkException(IReadOnlyList<string> errors) : base(string.Join(Environment.NewLine, errors))
    {
        Errors = errors;
    }
}
