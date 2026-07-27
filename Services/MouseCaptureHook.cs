using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MacroFenetre.Services;

internal sealed class MouseCaptureHook : IDisposable
{
    private readonly NativeMethods.HookProc _callback;
    private readonly Action<NativeMethods.Point> _captured;
    private nint _hookHandle;
    private bool _hasCaptured;
    private NativeMethods.Point _capturedPoint;

    internal MouseCaptureHook(Action<NativeMethods.Point> captured)
    {
        _captured = captured;
        _callback = HookCallback;
        _hookHandle = NativeMethods.SetWindowsHookEx(
            NativeMethods.WhMouseLl,
            _callback,
            NativeMethods.GetModuleHandle(null),
            0);

        if (_hookHandle == nint.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Impossible de démarrer la capture du clic.");
        }
    }

    public void Dispose()
    {
        if (_hookHandle == nint.Zero)
        {
            return;
        }

        NativeMethods.UnhookWindowsHookEx(_hookHandle);
        _hookHandle = nint.Zero;
        GC.SuppressFinalize(this);
    }

    private nint HookCallback(int code, nint message, nint data)
    {
        if (code >= 0 && message == NativeMethods.WmLButtonDown)
        {
            if (!_hasCaptured)
            {
                _hasCaptured = true;
                var hookData = Marshal.PtrToStructure<NativeMethods.MouseHookData>(data);
                _capturedPoint = hookData.Position;
            }

            return 1;
        }

        if (code >= 0 && message == NativeMethods.WmLButtonUp && _hasCaptured)
        {
            _captured(_capturedPoint);
            return 1;
        }

        return NativeMethods.CallNextHookEx(_hookHandle, code, message, data);
    }
}
