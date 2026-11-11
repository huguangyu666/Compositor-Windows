using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Input;

namespace Compositor.App;

internal static class CanvasCursors
{
    internal static unsafe InputCursor CreateCircle()
    {
        const int side = 32, center = 16;
        var andPlane = new byte[side * side / 8]; Array.Fill(andPlane, (byte)255);
        var xorPlane = new byte[andPlane.Length];
        for (int y = 0; y < side; y++)
            for (int x = 0; x < side; x++)
            {
                double edge = Math.Abs(Math.Sqrt((x - center) * (x - center) + (y - center) * (y - center)) - 11);
                if (edge > 1.5) continue;
                int offset = y * (side / 8) + x / 8; byte bit = (byte)(128 >> (x % 8));
                andPlane[offset] &= (byte)~bit;
                if (edge <= .5) xorPlane[offset] |= bit;
            }
        nint native = CreateCursor(0, center, center, side, side, andPlane, xorPlane);
        if (native == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        nint result = 0;
        try
        {
            // Windows App SDK's IInputCursorStaticsInterop copies the custom HCURSOR.
            // https://learn.microsoft.com/windows/windows-app-sdk/api/win32/microsoft.ui.input.inputcursor.interop/nf-microsoft-ui-input-inputcursor-interop-iinputcursorstaticsinterop-createfromhcursor
            using var factory = WinRT.ActivationFactory.Get("Microsoft.UI.Input.InputCursor",
                new Guid("AC6F5065-90C4-46CE-BEB7-05E138E54117"));
            nint instance = factory.ThisPtr;
            var create = (delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)(*(nint**)instance)[6];
            Marshal.ThrowExceptionForHR(create(instance, native, &result));
            return WinRT.MarshalInterface<InputCursor>.FromAbi(result);
        }
        finally
        {
            if (result != 0) Marshal.Release(result);
            DestroyCursor(native);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint CreateCursor(nint instance, int hotX, int hotY, int width, int height, byte[] andPlane, byte[] xorPlane);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyCursor(nint cursor);
}
