using System.Text;
using Corsac.Lang.Lto;

namespace Corsac.Tests.Elf;

/// <summary>
/// A backend's error reply: whatever it failed on is said whole or cut,
/// never a second failure in the saying (BackendProtocol.Fitted) -- a text
/// over the protocol's length, a NUL, a lone surrogate the strict encoder
/// cannot write.
/// </summary>
public static class BackendReplyTests
{
    public static void Run()
    {
        void Check(bool value, string message) { if (!value) throw new Exception(message); }

        string Said(string error)
        {
            using MemoryStream stream = new();
            using (BinaryWriter writer = new(stream, BackendProtocol.Utf8, leaveOpen: true))
                BackendProtocol.WriteResponse(writer, error);
            stream.Position = 0;
            using BinaryReader reader = new(stream, BackendProtocol.Utf8);
            try { BackendProtocol.ReadResponse(reader); }
            catch (InvalidDataException failed) when (failed.Message.StartsWith("Compiler backend: ", StringComparison.Ordinal))
            {
                return failed.Message["Compiler backend: ".Length..];
            }
            throw new Exception("an error reply was not read as the backend's error");
        }

        Check(Said("plain error") == "plain error", "a short error is not said as it was");
        Check(Said("a\0b") == "a\\0b", "a NUL is not spelled out");
        Check(Said("x\uD800y") == "x\\uD800y", "a lone surrogate is not spelled out");
        Check(Said("pair 😀 kept") == "pair 😀 kept", "a surrogate pair is not kept");

        // Whole whatever fits, to the last byte: no room kept for words it
        // never needs, a spelled-out NUL included.
        string exact = new string('n', BackendProtocol.MaxText);
        Check(Said(exact) == exact, "an error of exactly the length was cut");
        string spelled = new string('n', BackendProtocol.MaxText - 2) + "\0";
        Check(Said(spelled) == new string('n', BackendProtocol.MaxText - 2) + "\\0", "a spelled-out error that fits was cut");

        // Cut: within the length, its start kept, its words saying how much
        // went -- and that count right.
        string Cut(string error, out int kept, out int gone)
        {
            string text = Said(error);
            int words = text.LastIndexOf("\n... (", StringComparison.Ordinal);
            Check(words > 0, "a cut error does not say it was cut");
            string count = text[(words + "\n... (".Length)..];
            gone = int.Parse(count[..count.IndexOf(' ')], System.Globalization.CultureInfo.InvariantCulture);
            kept = words;
            Check(Encoding.UTF8.GetByteCount(text) <= BackendProtocol.MaxText, "a cut error is over the protocol's length");
            Check(text.EndsWith(" more characters cut: a backend reply holds 65536 bytes)", StringComparison.Ordinal), "a cut error's words are not the protocol's");
            return text[..words];
        }

        string huge = new string('e', 70000);
        string start = Cut(huge, out int keptHuge, out int goneHuge);
        Check(start == huge[..keptHuge] && keptHuge + goneHuge == huge.Length, "a cut error's count is not what went");
        Check(keptHuge > BackendProtocol.MaxText - 100, "a cut error kept less than the room it had");

        // Two- and three-byte characters and pairs up to the cut: never part
        // of one, and the count in characters.
        foreach (string unit in new[] { "\u00e9", "\u20ac", "\uD83D\uDE00" })
        {
            string many = string.Concat(Enumerable.Repeat(unit, 70000 / unit.Length));
            string kept = Cut(many, out int keptMany, out int goneMany);
            Check(kept == many[..keptMany] && keptMany % unit.Length == 0, "a cut split a character: " + (int)unit[0]);
            Check(keptMany + goneMany == many.Length, "a cut's count is wrong over " + (int)unit[0]);
        }

        // Something to spell out in the part that is cut away: counted as
        // the characters it was.
        string tail = new string('t', 70000) + "\0\uD800";
        Cut(tail, out int keptTail, out int goneTail);
        Check(keptTail + goneTail == tail.Length, "a cut's count over spelled-out characters is wrong");
    }
}
