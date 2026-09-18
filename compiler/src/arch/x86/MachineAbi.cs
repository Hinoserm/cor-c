#nullable enable

namespace Corsac;

/// <summary>
/// CORSAC architectural numbers shared by the assembler, compiler, runtime,
/// and processor implementations.
///
/// This file is deliberately free of emulator types. It is part of the
/// portable compiler source set: a compiler running on CORSAC must know the
/// machine ABI without importing the host implementation of the machine.
/// </summary>
public static class MachineAbi
{
    // Control-register selectors.
    public const int CtlVecBase = 0;
    public const int CtlLevel = 1;
    public const int CtlElr = 2;
    public const int CtlCause = 3;
    public const int CtlDetail = 4;
    public const int CtlSavedLevel = 5;
    public const int CtlEoi = 6;
    public const int CtlArena = 7;
    public const int CtlArenaEnd = 8;
    public const int CtlIrr = 9;
    public const int CtlIsr = 10;
    public const int CtlImr = 11;
    public const int CtlPending = 12;
    public const int CtlRing = 13;
    public const int CtlSavedRing = 14;
    public const int CtlIoPerm = 15;
    public const int CtlHandler = 16;
    public const int CtlThreadPointer = 17;
    public const int CtlIdles = 18;
    public const int CtlPageTable = 19;
    public const int CtlMmuCtl = 20;
    public const int CtlFaultAddr = 21;
    public const int CtlFaultStatus = 22;
    public const int CtlLibraryBase = 23;
    public const int CtlLibBase = CtlLibraryBase;
    public const int CtlAsid = 24;
    public const int CtlSecondaryPageTable = 25;
    public const int CtlSecondaryAsid = 26;
    public const int CtlMachineCheckLink = 27;
    public const int CtlMachineCheckSavedState = 28;
    public const int CtlMachineCheckCause = 29;
    public const int CtlMachineCheckAddress = 30;
    public const int CtlMachineCheckInfo = 31;
    public const int CtlVectorBase1 = 32;
    public const int CtlVectorBase2 = 33;
    public const int CtlVectorBase3 = 34;
    public const int CtlVectorBase4 = 35;
    public const int CtlVectorBase5 = 36;
    public const int CtlVectorBase6 = 37;
    public const int CtlVectorBase7 = 38;
    public const int CtlVectorPolicy = 39;
    public const int CtlGateBase = 40;
    public const int CtlGateCount = 41;
    public const int CtlGateStatus = 42;
    public const int CtlPhysicalProtectionStatus = 43;
    public const int CtlFaultPhysicalAddress = 44;
    public const int CtlFaultPhysicalStatus = 45;
    public const int CtlEntryStackTag = 46;
    public const int CtlProtectedExecutionFeatures = 47;
    public const int CtlFirmwareTable = 48;
    public const int CtlFirmwareTableBytes = 49;
    public const int CtlBootStackBase = 50;
    public const int CtlBootStackBytes = 51;
    public const int CtlUserSp = 52;
    public const int CtlKernelSp = 53;
    public const int CtlMachineCheckVector = 54;
    public const int CtlMachineCheckControl = 55;
    public const int CtlProcessorPointer = 56;
    public const int CtlSelf = CtlProcessorPointer;
    public const int CtlManagedBarrier = 57;
    public const int CtlCount = 64;

    // TLBINV mode field.
    public const int TlbInvPageAsid = 0;
    public const int TlbInvAsid = 1;
    public const int TlbInvRangeAsid = 2;
    public const int TlbInvAllNonGlobal = 3;
    public const int TlbInvAll = 4;

    // Programmable timer register selectors and control bits.
    public const int TimerCount = 0;
    public const long TimerEnable = 1L << 0;
    public const long TimerPeriodic = 1L << 1;
    public const int TimerPicInputShift = 2;
    public const long TimerPicInputMask = 3L << TimerPicInputShift;
    public const long TimerWake = 1L << 4;
    public const long TimerCountWhileParked = 1L << 5;
    public const long TimerCoalesce = 1L << 6;

    public static int TimerCompare(int channel) => 1 + 4 * channel;
    public static int TimerPeriod(int channel) => 2 + 4 * channel;
    public static int TimerControl(int channel) => 3 + 4 * channel;
    public static int TimerStatus(int channel) => 4 + 4 * channel;

    // Stable bare-host service numbers. The operating-system syscall layer
    // deliberately uses the same exit number but otherwise remains separate.
    public const int HostInvalid = 0;
    public const int HostExit = 1;
    public const int HostPrintInt = 2;
    public const int HostPrintStr = 3;
    public const int HostPrintChar = 4;
    public const int HostPrintHex = 5;
    public const int HostPrintLine = 6;
    public const int HostPrintF = 7;

    // Space retained below the standalone program stack. This is an image ABI
    // policy used by generated entry stubs, not a ScriptRuntime detail.
    public const int StandaloneStackReserve = 64 * 1024;
}
