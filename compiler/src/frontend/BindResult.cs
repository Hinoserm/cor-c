#nullable enable
namespace Corsac.Lang;

public sealed partial class BindResult
{
    /// <summary>
    /// Drop the completed analysis after specialization has consumed it.
    /// Only the final binding is needed by lowering. Clear references even
    /// if a conservative stack root temporarily retains this result object.
    /// Does not mutate the AST or the symbols that the next round will read.
    /// </summary>
    public void ReleaseForRebind()
    {
        StaticInits.Clear();
        Wanted.Clear();
        Views.Clear();
        ArrayViews.Clear();
        Boxes.Clear();
        Closures.Clear();
        ExprType.Clear();
        Resolved.Clear();
        Calls.Clear();
        Chained.Clear();
        Methods.Clear();
        FrameSize.Clear();
        LocalSlot.Clear();
        LocalType.Clear();
        LocalSymbols.Clear();
        BoxedLocals.Clear();
        PatternSlot.Clear();
        NullablePatterns.Clear();
        ValuePatterns.Clear();
        StringTests.Clear();
        TestedTypes.Clear();
        ArmSlot.Clear();
        PatternSym.Clear();
        ArmTests.Clear();
        SwitchSlot.Clear();
        PatternSubject.Clear();
        InitField.Clear();
        InitSetter.Clear();
        InitGetter.Clear();
        Rewrites.Clear();
        InitAdder.Clear();
        NewConstructors.Clear();
        InitIndexer.Clear();
        Tuples.Clear();
        Lowered.Clear();
        Indexers.Clear();
        IndexSetters.Clear();
        PropertySetters.Clear();
        SizeOfs.Clear();
        TypeOfs.Clear();
        GetTypes.Clear();
        Invocations.Clear();
        Receivers.Clear();
        ForeachSlot.Clear();
        SwitchSubject.Clear();
        GotoCases.Clear();
        DiscardAssignments.Clear();
        CapturedReceivers.Clear();
        EnumHasFlags.Clear();
        EnumStatics.Clear();
        CatchType.Clear();
        CatchSlot.Clear();
        Constants.Clear();
        ConstantTypes.Clear();
        TextConstants.Clear();
        AddressOf.Clear();
        Awaits.Clear();
        Errors.Clear();
        Warnings.Clear();
        Types.Clear();
    }

    /// <summary>
    /// Separate mutable result containers from declaration-time containers.
    /// Symbols and AST nodes retain identity; this is not a deep symbol freeze
    /// and does not by itself make body checking safe to run concurrently.
    /// </summary>
    public BindResult CopyForBodyChecking()
    {
        BindResult copy = new()
        {
            MaskWords = MaskWords, ToStringSlot = ToStringSlot,
            EqualsSlot = EqualsSlot, HashSlot = HashSlot,
            CompareSlot = CompareSlot, StaticBytes = StaticBytes,
        };
        copy.StaticInits.AddRange(StaticInits);
        copy.Wanted.AddRange(Wanted);
        CopyEntries(Views, copy.Views);
        copy.ArrayViews.AddRange(ArrayViews);
        foreach (var item in Boxes) copy.Boxes.Add(item);
        CopyEntries(Closures, copy.Closures);
        CopyEntries(ExprType, copy.ExprType);
        CopyEntries(Resolved, copy.Resolved);
        CopyEntries(Calls, copy.Calls);
        CopyEntries(Chained, copy.Chained);
        CopyEntries(Methods, copy.Methods);
        CopyEntries(FrameSize, copy.FrameSize);
        CopyEntries(LocalSlot, copy.LocalSlot);
        CopyEntries(LocalType, copy.LocalType);
        CopyEntries(LocalSymbols, copy.LocalSymbols);
        foreach (var item in BoxedLocals) copy.BoxedLocals.Add(item);
        CopyEntries(PatternSlot, copy.PatternSlot);
        foreach (var item in NullablePatterns) copy.NullablePatterns.Add(item);
        foreach (var item in ValuePatterns) copy.ValuePatterns.Add(item);
        foreach (var item in StringTests) copy.StringTests.Add(item);
        CopyEntries(TestedTypes, copy.TestedTypes);
        CopyEntries(ArmSlot, copy.ArmSlot);
        CopyEntries(PatternSym, copy.PatternSym);
        foreach (var item in ArmTests) copy.ArmTests.Add(item);
        CopyEntries(SwitchSlot, copy.SwitchSlot);
        CopyEntries(PatternSubject, copy.PatternSubject);
        CopyEntries(InitField, copy.InitField);
        CopyEntries(InitSetter, copy.InitSetter);
        CopyEntries(InitGetter, copy.InitGetter);
        CopyEntries(Rewrites, copy.Rewrites);
        CopyEntries(InitAdder, copy.InitAdder);
        CopyEntries(NewConstructors, copy.NewConstructors);
        CopyEntries(InitIndexer, copy.InitIndexer);
        CopyEntries(Tuples, copy.Tuples);
        CopyEntries(Lowered, copy.Lowered);
        CopyEntries(Indexers, copy.Indexers);
        CopyEntries(IndexSetters, copy.IndexSetters);
        CopyEntries(PropertySetters, copy.PropertySetters);
        CopyEntries(SizeOfs, copy.SizeOfs);
        CopyEntries(TypeOfs, copy.TypeOfs);
        foreach (var item in GetTypes) copy.GetTypes.Add(item);
        CopyEntries(Invocations, copy.Invocations);
        CopyEntries(Receivers, copy.Receivers);
        CopyEntries(ForeachSlot, copy.ForeachSlot);
        CopyEntries(SwitchSubject, copy.SwitchSubject);
        CopyEntries(GotoCases, copy.GotoCases);
        foreach (var item in DiscardAssignments) copy.DiscardAssignments.Add(item);
        CopyEntries(CapturedReceivers, copy.CapturedReceivers);
        foreach (var item in EnumHasFlags) copy.EnumHasFlags.Add(item);
        CopyEntries(EnumStatics, copy.EnumStatics);
        CopyEntries(CatchType, copy.CatchType);
        CopyEntries(CatchSlot, copy.CatchSlot);
        CopyEntries(Constants, copy.Constants);
        CopyEntries(ConstantTypes, copy.ConstantTypes);
        CopyEntries(TextConstants, copy.TextConstants);
        CopyEntries(AddressOf, copy.AddressOf);
        CopyEntries(Awaits, copy.Awaits);
        copy.Errors.AddRange(Errors);
        copy.Warnings.AddRange(Warnings);
        CopyEntries(Types, copy.Types);
        return copy;
    }

    private static void CopyEntries<K, V>(Dictionary<K, V> source, Dictionary<K, V> destination)
        where K : notnull
    {
        foreach (KeyValuePair<K, V> entry in source)
            destination.Add(entry.Key, entry.Value);
    }
}
