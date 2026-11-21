using System.Runtime.InteropServices;

namespace NetPrio.Services;

internal static class WinDivertNative
{
    private const string DllName = "WinDivert.dll";

    public const int WINDIVERT_LAYER_NETWORK = 0;
    public const int ADDR_SIZE = 80;
    public static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [StructLayout(LayoutKind.Explicit, Size = 80)]
    public struct WINDIVERT_ADDRESS
    {
        [FieldOffset(0)] public long Timestamp;
        [FieldOffset(8)] public byte Layer;
        [FieldOffset(9)] public byte Event;
        [FieldOffset(10)] public byte Flags; // bit 1 = Outbound
        [FieldOffset(16)] public uint IfIdx;
        [FieldOffset(20)] public uint SubIfIdx;

        public bool IsOutbound => (Flags & 0x02) != 0;
    }

    [DllImport(DllName, SetLastError = true, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern IntPtr WinDivertOpen(string filter, int layer, short priority, ulong flags);

    [DllImport(DllName, SetLastError = true, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinDivertRecvEx(
        IntPtr handle,
        byte[] pPacket,
        uint packetLen,
        out uint pRecvLen,
        ulong flags,
        [Out] WINDIVERT_ADDRESS[] pAddr,
        ref uint pAddrLen,
        IntPtr lpOverlapped);

    [DllImport(DllName, SetLastError = true, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinDivertSendEx(
        IntPtr handle,
        byte[] pPacket,
        uint packetLen,
        out uint pSendLen,
        ulong flags,
        [In] WINDIVERT_ADDRESS[] pAddr,
        uint addrLen,
        IntPtr lpOverlapped);

    [DllImport(DllName, SetLastError = true, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinDivertHelperCalcChecksums(
        IntPtr pPacket,
        uint packetLen,
        ref WINDIVERT_ADDRESS pAddr,
        ulong flags);

    [DllImport(DllName, SetLastError = true, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinDivertClose(IntPtr handle);
}
