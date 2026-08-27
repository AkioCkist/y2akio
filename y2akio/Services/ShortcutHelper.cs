using System;
using System.Runtime.InteropServices;

namespace y2akio.Services;

/// <summary>
/// Creates a Start-menu shortcut carrying an AppUserModelID. Windows only displays toasts for
/// unpackaged apps that have such a shortcut (registry AUMID alone is not enough).
/// </summary>
internal static class ShortcutHelper
{
    private static readonly Guid ShellLinkClsid = new("00021401-0000-0000-C000-000000000046");
    private static readonly Guid ShellLinkIid = new("000214F9-0000-0000-C000-000000000046");
    private static readonly Guid PersistFileIid = new("0000010B-0000-0000-C000-000000000046");
    private static readonly Guid PropertyStoreIid = new("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99");

    // PKEY_AppUserModel_ID
    private static readonly Guid AppUserModelIdFmtId = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
    private const uint AppUserModelIdPid = 5;

    public static void Create(string shortcutPath, string targetPath, string appUserModelId)
    {
        var link = (IShellLinkW)Activator.CreateInstance(
            Type.GetTypeFromCLSID(ShellLinkClsid)!, args: null)!;
        try
        {
            link.SetPath(targetPath);
            link.SetWorkingDirectory(System.IO.Path.GetDirectoryName(targetPath));
            link.SetIconLocation(targetPath, 0);

            // Set System.AppUserModel.ID on the .lnk so the shell associates toasts with us.
            // Must happen before Save or the property is lost.
            var props = (IPropertyStore)link;
            var key = new PROPERTYKEY(AppUserModelIdFmtId, AppUserModelIdPid);
            var value = new PROPVARIANT { vt = VT_LPWSTR, pwszVal = Marshal.StringToCoTaskMemUni(appUserModelId) };
            props.SetValue(ref key, ref value);
            props.Commit();

            var persist = (IPersistFile)link;
            persist.Save(shortcutPath, true);
        }
        finally
        {
            Marshal.ReleaseComObject(link);
        }
    }

    private const ushort VT_LPWSTR = 31;

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
        public PROPERTYKEY(Guid fmtid, uint pid) { this.fmtid = fmtid; this.pid = pid; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPVARIANT
    {
        public ushort vt;
        public ushort wReserved1;
        public ushort wReserved2;
        public ushort wReserved3;
        public IntPtr pwszVal; // union head; VT_LPWSTR reads this
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath(
            [Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszFile,
            int cch,
            IntPtr pfd,
            int fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out ushort pwHotkey);
        void SetHotkey(ushort wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
        void Resolve(IntPtr hwnd, int fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("0000010B-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        void IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, int dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void GetCurFile([Out, MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
    private interface IPropertyStore
    {
        void GetCount(out uint cProps);
        void GetAt(uint iProp, out PROPERTYKEY pkey);
        void GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
        void SetValue(ref PROPERTYKEY key, ref PROPVARIANT pv);
        void Commit();
    }
}
