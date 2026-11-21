using System.Runtime.InteropServices;

namespace NetPrio.Services;

internal static class WinDivertNative
{
    private const string DllName = "WinDivert.dll";

    public const int WINDIVERT_LAYER_NETWORK = 0;
    public static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [StructLayout(LayoutKind.Explicit, Size = 80)]
    public struct WINDIVERT_ADDRESS
    {
        [FieldOffset(0)] public long Timestamp;
        [FieldOffset(8)] public byte Layer;
        [FieldOffset(9)] public byte Event;
        [FieldOffset(10)] public byte Flags; // bit 1 = Outbound, bit 2 = Loopback
        [FieldOffset(16)] public uint IfIdx;
        [FieldOffset(20)] public uint SubIfIdx;

        public bool IsOutbound => (Flags & 0x02) != 0;
    }

    [DllImport(DllName, SetLastError = true, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern IntPtr WinDivertOpen(string filter, int layer, short priority, ulong flags);

    [DllImport(DllName, SetLastError = true, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinDivertRecv(IntPtr handle, byte[] pPacket, uint packetLen, out uint pRecvLen, out WINDIVERT_ADDRESS pAddr);

    [DllImport(DllName, SetLastError = true, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinDivertSend(IntPtr handle, byte[] pPacket, uint packetLen, out uint pSendLen, ref WINDIVERT_ADDRESS pAddr);

    [DllImport(DllName, SetLastError = true, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinDivertHelperCalcChecksums(byte[] pPacket, uint packetLen, ref WINDIVERT_ADDRESS pAddr, ulong flags);

    [DllImport(DllName, SetLastError = true, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinDivertClose(IntPtr handle);
}
