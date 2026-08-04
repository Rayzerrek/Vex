using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Vex.Terminal.Native;

namespace Vex.Terminal;

/// <summary>
/// Reads another process's command line by walking its PEB
/// (NtQueryInformationProcess → PEB.ProcessParameters → CommandLine, then
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
    public static string? Get(uint pid)
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
            var buffer = new byte[commandLine.Length];
            if (!NativeMethods.ReadProcessMemory(handle, commandLine.Buffer, buffer, buffer.Length, out var read) || read == 0)
                return null;
            return Encoding.Unicode.GetString(buffer, 0, read);
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    private static bool ReadPointer(IntPtr handle, IntPtr address, out IntPtr value)
    {
        var bytes = new byte[IntPtr.Size];
        if (!NativeMethods.ReadProcessMemory(handle, address, bytes, bytes.Length, out var read) || read != bytes.Length)
        {
            value = IntPtr.Zero;
            return false;
        }
        value = IntPtr.Size == 8
            ? new IntPtr(BinaryPrimitives.ReadInt64LittleEndian(bytes))
            : new IntPtr(BitConverter.ToInt32(bytes, 0));
        return true;
    }

    private static bool ReadUnicodeString(IntPtr handle, IntPtr address, out (ushort Length, IntPtr Buffer) commandLine)
    {
        var bytes = new byte[16];
        if (!NativeMethods.ReadProcessMemory(handle, address, bytes, bytes.Length, out var read) || read != bytes.Length)
        {
            commandLine = default;
            return false;
        }
        var length = BinaryPrimitives.ReadUInt16LittleEndian(bytes);
        var buffer = IntPtr.Size == 8
            ? new IntPtr(BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(8)))
            : new IntPtr(BitConverter.ToInt32(bytes, UnicodeStringBufferOffset));
        commandLine = (length, buffer);
        return true;
    }
}
