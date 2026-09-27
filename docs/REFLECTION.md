# Reflection

Full `System.Reflection`, compiled ahead of time, paid for only by the images that use
it. A program that never asks a `Type` about its members carries no reflection tables,
runs no reflection code at startup, and has descriptors exactly as large as today's. A
program that does ask gets read-only tables for the types it can reach, built at link
time from records every object already carries.

CORSAC is unreleased and sealed: this design changes the descriptor, the object format,
the link and the library outright. Nothing is kept for older objects; a stale object is
rejected by the ABI version, not translated.

## What there is today

`Type` is a primitive: the address of a type's descriptor. `typeof(T)`, `GetType()` and
`==` on two of them work, and `Name`, `FullName` and `Assembly` are answered by the
compiler (Binder, `m.Name is "Name" or "FullName"`). `is`, `as`, casts and type
switches read the descriptor's display and interface list. `Enum.GetNames`, `GetValues`,
`GetName`, `IsDefined`, `Parse` and `TryParse` are written out at compile time, because
the compiler knows every member. Nothing else of `System.Reflection` exists: no
`BaseType`, no `GetProperties`, no `MethodInfo.Invoke`, no `Activator`, no attribute
survives compilation.

## The three tiers

**Tier 0 -- the type itself. Always present, free.** Everything answerable from the
descriptor that every type already has: `Name`, `FullName`, `Namespace`, `BaseType`,
`IsClass`, `IsValueType`, `IsInterface`, `IsEnum`, `IsArray`, `IsSealed`,
`IsAbstract`, `GetElementType`, `GetInterfaces`, `IsAssignableFrom`, `IsSubclassOf`,
`IsInstanceOfType`, `IsGenericType`, `GetGenericTypeDefinition` (as a name). These are
ordinary library code over the descriptor words; the only compiler work is widening the
flags word to say what kind of type it is (below). A program that uses only tier 0 has
no tables at all.

**Tier 1 -- members, described.** `GetFields`, `GetProperties`, `GetMethods`,
`GetConstructors`, `GetEvents`, `GetMember`, their single-name forms and `BindingFlags`
filtering; each member's name, declared type, parameter list, modifiers and custom
attributes; `Enum` members at run time; `Type.GetType(string)` and
`Assembly.GetTypes()`. Needs the member tables, not the code behind them.

**Tier 2 -- members, used.** `FieldInfo.GetValue/SetValue`, `PropertyInfo.GetValue/SetValue`,
`MethodInfo.Invoke`, `ConstructorInfo.Invoke`, `Activator.CreateInstance`,
`Delegate.CreateDelegate`, `GetCustomAttributes` (which constructs attribute objects).
Needs the tables AND the code they point at: accessor thunks, and every member body
they reach kept out of dead-code removal.

## Paying only for use

### Capability roots

Tier 1 and tier 2 are reached only through library entry points, and each entry point
names the capability it needs by referencing one well-known symbol:

| Symbol | Referenced by | Materialises |
|---|---|---|
| `__reflect_members` | every tier-1 API (`Type.GetProperties`, `GetMethods`, ...) | member tables |
| `__reflect_names` | `Type.GetType(string)`, `Assembly.GetTypes()` | the name index |
| `__reflect_access` | `GetValue`, `SetValue`, `Invoke`, `Activator`, `CreateDelegate` | accessor thunks; retains bodies |
| `__reflect_attributes` | `GetCustomAttributes`, `IsDefined` | attribute blobs and their constructors |

The symbols are defined by the final link, not by any object. The link decides reflection
AFTER dead-code removal: a capability is present exactly when some retained function
references its symbol. A program that calls `typeof(Foo).Name` and nothing else keeps no
tier-1 entry point, references none of the four, and the link writes no table.

### Records travel in every object; tables exist only in images that ask

Each unit writes a compact **reflection record** for every type it defines, in a
non-allocated section `.corsac.reflect` beside `.corsac.coalesce` and the frame tables.
It is built from what the binder already has (fields and their offsets, properties and
their accessor symbols, methods and their symbols, constructors, attributes) and costs
the unit a few hundred bytes per type. It is never loaded at run time.

The final link then does one of two things:

- **No capability referenced** -- drops every `.corsac.reflect` section. The image is
  byte-identical to one built with reflection records never written.
- **Some capability referenced** -- materialises the records of the REFLECTABLE types
  (below) into `.rodata.reflect`, writes the columns the referenced capabilities need,
  generates the accessor thunks if `__reflect_access` is live, and fills each reflectable
  type's descriptor word 10 with its table's address.

Doing it at link time is what makes "only if used" exact under separate compilation: the
unit that defines `Foo` cannot know whether another unit reflects on it, and the link is
the first place that sees the whole image.

### Which types are reflectable

In an image that uses reflection:

1. **Every type whose descriptor survives the link** -- it was constructed, boxed, named
   by `typeof`, or tested by `is`. A type no retained code can reach cannot be handed to
   reflection by anything but a name lookup (`Type.GetType`), and those are covered by (3).
2. **Its members, filtered by `[DynamicallyAccessedMembers]`** where C# code says what it
   will reflect on (`void Bind<[DynamicallyAccessedMembers(PublicProperties)] T>()`,
   `Type` parameters and fields carrying the attribute) -- .NET's own trimming annotation,
   so real .NET code already has them. Where nothing narrows it, all members of the type.
3. **Types named by `[DynamicDependency]`, by a `--reflect-root <Type>` option, or by a
   `reflect.roots` file** -- for types only ever reached by `Type.GetType(string)`.

Tier-2 retention follows the same set: with `__reflect_access` live, the accessor thunk of
every member in a reflectable type's table is a root, and so is the body it calls. Without
it, the table lists the member and points at no code, so describing a method never keeps
its body alive.

### Libraries

A static archive needs no flag: its objects carry records and the program's link decides.

A **shared library** cannot know its consumers, so it decides at its own link:

- `--reflection=auto` (the default): exactly as a program -- tables only if the library
  itself reflects.
- `--reflection=public`: materialise tables for every public type and its public
  members, with thunks, whether or not the library reflects. For a library that
  consumers will reflect over (a serializer's models, a plug-in host's contracts).
- `--reflection=all`: every type and member, public or not.
- `--reflection=none`: refuse; a reference to a capability symbol is a link error naming
  the function that made it.

A program may also say `--reflection=public|all|none` to override `auto`.

## The formats

### Descriptor

Word 10 (`DescReflect`) of the 12-word descriptor (48 bytes on x86-32, 96 on x86-64 --
words 10 and 11 are unused today) holds the address of the type's table, or zero. The
flags word (6) gains the type's kind and modifiers, which tier 0 needs and which cost
nothing because the word exists:

| Bits | Meaning |
|---|---|
| 0-3 | kind: class, struct, interface, enum, array, string, delegate, box, tuple |
| 4 | sealed |
| 5 | abstract |
| 6 | generic instantiation |
| 7 | nested |
| 8-15 | enum underlying primitive, or array rank |

The existing values 1 (array) and 3 (string) are replaced by this encoding; every reader
changes with it.

### Type table (`.rodata.reflect`)

One per reflectable type, word-aligned, all references relocated:

```
TypeTable
  descriptor        word   back to the descriptor
  namespace         word   interned string ("" for none)
  declaringType     word   descriptor of the enclosing type, or 0
  genericDefinition word   the template's TypeTable for an instantiation, or 0
  genericArguments  word   array of descriptors, or 0
  fieldCount, propertyCount, methodCount, constructorCount, eventCount   u16 each
  attributes        word   AttributeList, or 0
  members           word   MemberTable: fields, properties, events, methods, constructors
  cache             u32    index into the image's info cache (below)
```

```
Member (fixed size; kind selects which words mean what)
  name        word   interned string -- the same literal the rest of the image uses
  flags       u32    kind, public/family/assembly/private, static, virtual, abstract,
                     readonly, init-only, special-name, has-default, params
  type        word   declared type: a descriptor, or a TypeSig for arrays/generics/primitives
  signature   word   ParameterList (methods, constructors, indexers), or 0
  target      word   field: byte offset; property/event: getter/add Member index pairs;
                     method: the method's own symbol (tier 2 only, else 0)
  thunk       word   tier-2 accessor thunk, or 0
  attributes  word   AttributeList, or 0
```

Names are the image's ordinary interned UTF-16 string literals, so `MemberInfo.Name` returns
a string without allocating. Members are sorted by name within each kind so a single-name
lookup (`GetProperty("Size")`) is a binary search.

**TypeSig** spells what a descriptor cannot: a primitive (a one-byte code whose Type is the
primitive's box descriptor, as `typeof(int)` already is), an array of a TypeSig, a
Nullable of one, a pointer, or a generic parameter by position.

**AttributeList** is a count and records of: the attribute's descriptor, its constructor's
Member, and an argument blob in ECMA-335's custom-attribute encoding (fixed arguments then
named ones). The blob is decoded only when `GetCustomAttributes` asks, so describing
attributes costs no code. Attributes the compiler itself consumes (`[DoesNotReturn]`,
`[NotNullIfNotNull]`, `[UnmanagedCallersOnly]`, `[Flags]` is kept) are pseudo-attributes
and are not written.

### Name index

With `__reflect_names` live, one sorted array of (full name, descriptor) for every
reflectable type, and one per generic template of (name with arity, instantiation list).
`Type.GetType("Corsac.Lang.Elf.ObjectFile")` is a binary search; a generic name with
arguments finds the instantiation if the image compiled it.

### Accessor thunks

Invocation must not interpret signatures at run time. The link generates one thunk per
**call shape** -- the tuple of (receiver kind, each parameter's representation, return
representation), where representation is word, 32-bit, 64-bit, float, double, struct of
N bytes, or reference -- not per member. A thunk takes `(object? target, object?[] args,
address)`, unboxes each argument through its box descriptor, calls `address`, and boxes the
result. Every method with that shape shares it; the `target` word of the Member is the
address passed in. Field access needs no thunk at all: offset plus TypeSig reads and boxes
directly. Constructors use the method thunk after allocating through the descriptor's size
word, exactly as `new` does.

Thunks are generated by the linker from the shape list the records carry, in the linker's
own code generator path for synthetic functions (as the unit directory is), so no unit ever
compiles a thunk that the image does not keep.

## The runtime and the library

`System.Type` stays a descriptor address. Its API is library code in `System` taking the
Type as its receiver, the way string methods are written (`Type.GetProperties(Type t,
BindingFlags b)` reached by `t.GetProperties(b)`), so `typeof(Foo)` still allocates nothing.

`MemberInfo`, `FieldInfo`, `PropertyInfo`, `MethodBase`, `MethodInfo`, `ConstructorInfo`,
`EventInfo`, `ParameterInfo` are real classes in `System.Reflection`, each a pointer to its
Member plus the declaring Type. They are built the first time a type's members are asked
for and cached: the image's **info cache** is one static array, sized by the link to the
number of reflectable types, indexed by the TypeTable's `cache` field, filled with
compare-and-swap. No dictionary, no hashing, no lock; a second call to `GetProperties`
returns the cached array's copy as .NET does.

Exceptions are .NET's: `TargetInvocationException` wraps what an invoked member throws,
`TargetParameterCountException`, `ArgumentException` for an argument that does not convert,
`MissingMethodException` from `Activator` without a matching constructor,
`NotSupportedException` for what an ahead-of-time image cannot do (below).

## What an ahead-of-time image cannot do

As with .NET's NativeAOT: `System.Reflection.Emit` and `DynamicMethod` do not exist;
`MakeGenericType` and `MakeGenericMethod` succeed only for instantiations the image compiled
(the name index lists them) and otherwise throw `NotSupportedException`; loading an
assembly from a file does not exist. A library that needs more says so at link time through
the capability symbols it references, not at run time.

## Costs

- **An image that does not reflect:** zero. No section, no descriptor change beyond word 10
  staying zero, no startup work, no code.
- **An image that does:** read-only data proportional to reflectable members (roughly 7 words
  per member plus names already interned); one static cache array; thunks per call shape,
  typically tens. No startup work: nothing is registered or scanned until the first tier-1
  call. `typeof`, `GetType`, `is` and all tier-0 queries cost what they cost today.
- **Per query:** first member request per type builds its infos (linear in members), then
  O(1) from the cache; single-name lookups are a binary search; `Invoke` is one thunk call
  plus boxing, as in .NET.
- **Compile time:** writing records is linear in declared members and happens in the pass
  that already writes the declaration index.

## Separate compilation, coalescing and shared libraries

Records are per unit and keyed by the descriptor symbol, so a generic instantiation made in
several units writes the same record several times; the coalescing contract of
`DefinitionSemantics` covers `.corsac.reflect` records as it covers descriptors, and the
link keeps one. A shared library's materialised tables are reached through its own
descriptors' word 10, so a program reflecting on a library type needs nothing from the
library's link beyond `--reflection=public` or `all` there. The image directory
(`__corsac_units`, MANAGED-IMAGE-METADATA.md) is not involved: tables hang off descriptors,
and the info cache is image-local.

## Work, in order

1. **Tier 0.** Descriptor flags encoding; `Type` members in the library over the descriptor;
   the Binder's special cases for `Name`/`FullName`/`Assembly` become ordinary library
   methods.
2. **Records.** Binder/lowering writes `.corsac.reflect` per unit; the object format and
   `DefinitionSemantics` learn the section; the linker parses and drops it.
3. **Tier 1.** Capability symbols; link-time materialisation of type and member tables and
   the name index; `System.Reflection` info classes, the cache, `BindingFlags`,
   `[DynamicallyAccessedMembers]` and `[DynamicDependency]` in the reachability pass.
4. **Attributes.** Records keep custom attributes; `GetCustomAttributes` decodes blobs and
   constructs through the tier-2 path.
5. **Tier 2.** Call-shape thunks in the linker; retention roots; `Invoke`, `GetValue`,
   `SetValue`, `Activator`, `CreateDelegate`.
6. **Libraries.** `--reflection=auto|none|public|all` for programs and shared libraries.

Each step is tested against real .NET: the same program run with `dotnet run` and compiled
by corc, .NET's output the expected output (tests/language), plus link checks that an image
not using reflection is byte-identical with and without records, and that one using only
tier 0 carries no `.rodata.reflect`.
