using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace y2akio.Services.Downloads;

/// <summary>
/// Marks the partial file as NTFS sparse. Segments write at offsets far into a preallocated file;
/// on a normal file NTFS zero-fills everything between the last written byte and that offset first
/// (seconds for multi-GB files, blocking the write). A sparse file skips the zero-fill.
/// </summary>
internal static class SparseFile
{
    private const uint FSCTL_SET_SPARSE = 0x000900C4;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice, uint dwIoControlCode, ref byte lpInBuffer, uint nInBufferSize,
        IntPtr lpOutBuffer, uint nOutBufferSize, out uint lpBytesReturned, IntPtr lpOverlapped);

    /// <summary>Best effort: returns false on non-NTFS volumes (FAT32/exFAT) or non-Windows.</summary>
    public static bool TrySetSparse(SafeFileHandle handle, bool sparse)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            byte flag = sparse ? (byte)1 : (byte)0;
            return DeviceIoControl(handle, FSCTL_SET_SPARSE, ref flag, 1, IntPtr.Zero, 0, out _, IntPtr.Zero);
        }
        catch
        {
            return false;
        }
    }
}
