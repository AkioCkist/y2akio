using System;
using System.Runtime.InteropServices;

namespace y2akio.Services;

/// <summary>
/// COM notification activator required by Windows App SDK for unpackaged apps: without a registered
/// COM server, AppNotificationManager.Register() throws "No COM servers are registered for this app".
/// The class is registered (CLSID → this exe) in ToastService so toast clicks can bring the app up.
/// </summary>
[ComVisible(true)]
[Guid("4A9E6A2C-8C1B-4E3D-9F5A-B7D2E1C8F0A3")]
[ClassInterface(ClassInterfaceType.None)]
public sealed class NotificationActivator : INotificationActivationCallback
{
    public void Activate(string appUserModelId, string invokedArgs, IntPtr data, uint size)
    {
        // The app is already running; we don't route deep links yet, so nothing to do here.
    }
}

[ComImport]
[Guid("23A50B64-AAF0-4A4C-B6AA-8C9A0CEBBD2F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface INotificationActivationCallback
{
    void Activate(
        [In, MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
        [In, MarshalAs(UnmanagedType.LPWStr)] string invokedArgs,
        [In] IntPtr data,
        [In] uint size);
}
