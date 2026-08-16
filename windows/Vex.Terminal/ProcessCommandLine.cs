using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Vex.Terminal.Native;

namespace Vex.Terminal;

/// <summary>
/// Reads another process's command line by walking its PEB
/// (NtQueryInformationProcess -> PEB.ProcessParameters -> CommandLine, then
/// ReadProcessMemory). Needed because node-shimmed CLIs (claude, pi,
/// antigravity, ...) all show up as node.exe; only the script they run says
/// which app they really are.
/// </summary>
public static class ProcessCommandLine
{
    // Offsets into the x64/x86 PEB and RTL_USER_PROCESS_PARAMETERS.
    private static readonly int ProcessParametersOffset = IntPtr.Size == 8 ? 0x20 : 0x10;
    private static readonly int CommandLineOffset = IntPtr.Size == 8 ? 0x70 : 0x40;
    private static readonly int UnicodeStringBufferOffset = IntPtr.Size == 8 ? 8 : 4;

    /// <summary>Full command line of the process, or null when unreadable.</summary>
    public static unsafe string? Get(uint pid)
    {
        var handle = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION |
            NativeMethods.PROCESS_QUERY_INFORMATION |
            NativeMethods.PROCESS_VM_READ,
            bInheritHandle: false,
            pid);
        if (handle == IntPtr.Zero)
            return null;

        try
        {
            var pbi = new NativeMethods.PROCESS_BASIC_INFORMATION();
            if (NativeMethods.NtQueryInformationProcess(handle, NativeMethods.ProcessBasicInformation,
                    ref pbi, Marshal.SizeOf<NativeMethods.PROCESS_BASIC_INFORMATION>(), out _) != 0)
                return null;
            if (pbi.PebBaseAddress == IntPtr.Zero)
                return null;

            if (!ReadPointer(handle, IntPtr.Add(pbi.PebBaseAddress, ProcessParametersOffset), out var processParameters))
                return null;
            if (!ReadUnicodeString(handle, IntPtr.Add(processParameters, CommandLineOffset), out var commandLine))
                return null;

            if (commandLine.Length == 0)
                return "";

            // Stack-allocate for typical command lines (up to 2KB) to eliminate GC heap churn
            if (commandLine.Length <= 2048)
            {
                byte* stackBuf = stackalloc byte[commandLine.Length];
                if (!NativeMethods.ReadProcessMemory(handle, commandLine.Buffer, stackBuf, commandLine.Length, out var read) || read == 0)
                    return null;
                return Encoding.Unicode.GetString(stackBuf, read);
            }
            else
            {
                var rented = ArrayPool<byte>.Shared.Rent(commandLine.Length);
                try
                {
                    fixed (byte* pBuf = rented)
                    {
                        if (!NativeMethods.ReadProcessMemory(handle, commandLine.Buffer, pBuf, commandLine.Length, out var read) || read == 0)
                            return null;
                        return Encoding.Unicode.GetString(rented, 0, read);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(rented);
                }
            }
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    private static unsafe bool ReadPointer(IntPtr handle, IntPtr address, out IntPtr value)
    {
        byte* bytes = stackalloc byte[IntPtr.Size];
        if (!NativeMethods.ReadProcessMemory(handle, address, bytes, IntPtr.Size, out var read) || read != IntPtr.Size)
        {
            value = IntPtr.Zero;
            return false;
        }
        var span = new ReadOnlySpan<byte>(bytes, IntPtr.Size);
        value = IntPtr.Size == 8
            ? new IntPtr(BinaryPrimitives.ReadInt64LittleEndian(span))
            : new IntPtr(BinaryPrimitives.ReadInt32LittleEndian(span));
        return true;
    }

    private static unsafe bool ReadUnicodeString(IntPtr handle, IntPtr address, out (ushort Length, IntPtr Buffer) commandLine)
    {
        byte* bytes = stackalloc byte[16];
        if (!NativeMethods.ReadProcessMemory(handle, address, bytes, 16, out var read) || read != 16)
        {
            commandLine = default;
            return false;
        }
        var span = new ReadOnlySpan<byte>(bytes, 16);
        var length = BinaryPrimitives.ReadUInt16LittleEndian(span);
        var buffer = IntPtr.Size == 8
            ? new IntPtr(BinaryPrimitives.ReadInt64LittleEndian(span[8..]))
            : new IntPtr(BinaryPrimitives.ReadInt32LittleEndian(span[UnicodeStringBufferOffset..]));
        commandLine = (length, buffer);
        return true;
    }
}
