#nullable enable
using System.Collections;
using System.Reflection;
using SystemType = System.Type;

namespace Corsac.Lang.Metadata;

/// <summary>
/// Every type a declaration's BODIES name, for prefetching declarations.
///
/// An imported template comes with its body, because a specialisation is
/// compiled from it, and that body names types its signature never mentions:
/// List's methods make a ListEnumerator and throw an
/// ArgumentOutOfRangeException. Those names are otherwise discovered only when
/// the body is bound -- one round later -- and answering a demand means
/// throwing the unit away and parsing, merging, monomorphising and binding it
/// again.
///
/// This is a PREFETCH and nothing depends on it being complete: a name it does
/// not find is demanded by the binder exactly as before, one round later. So
/// it walks the tree reflectively rather than naming seventy-seven node types
/// it would have to be kept in step with, and only ever reports a
/// <see cref="TypeRef"/> -- a position the parser already decided is a type.
/// </summary>
public static class BodyTypeNames
{
    private static readonly Dictionary<SystemType, PropertyInfo[]> Shape = new();
    private static readonly object Gate = new();

    private static PropertyInfo[] PropertiesOf(SystemType type)
    {
        lock (Gate)
        {
            if (Shape.TryGetValue(type, out PropertyInfo[]? known)) return known;
            PropertyInfo[] found = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.GetIndexParameters().Length == 0 && Carries(property.PropertyType))
                .ToArray();
            Shape[type] = found;
            return found;
        }
    }

    /// <summary>Whether a property could hold nodes: one, or a collection of them.</summary>
    private static bool Carries(SystemType type)
    {
        if (typeof(Node).IsAssignableFrom(type)) return true;
        if (type == typeof(string) || type.IsPrimitive) return false;
        return typeof(IEnumerable).IsAssignableFrom(type);
    }

    /// <summary>Hands every type reference under a declaration to <paramref name="found"/>.</summary>
    public static void Walk(TypeDecl declaration, Action<TypeRef> found)
    {
        HashSet<Node> seen = new(ReferenceEqualityComparer.Instance as IEqualityComparer<Node>
            ?? EqualityComparer<Node>.Default);
        void Visit(object? value)
        {
            switch (value)
            {
                case null: return;
                case string: return;
                case TypeRef reference:
                    if (!seen.Add(reference)) return;
                    found(reference);
                    foreach (TypeRef argument in reference.Args) Visit(argument);
                    if (reference.UseArgs is not null) foreach (TypeRef argument in reference.UseArgs) Visit(argument);
                    return;
                case Node node:
                    if (!seen.Add(node)) return;
                    foreach (PropertyInfo property in PropertiesOf(node.GetType()))
                    {
                        object? child;
                        try { child = property.GetValue(node); }
                        catch (TargetInvocationException) { continue; }
                        Visit(child);
                    }
                    return;
                case IEnumerable many:
                    foreach (object? item in many) Visit(item);
                    return;
            }
        }
        Visit(declaration);
    }
}
