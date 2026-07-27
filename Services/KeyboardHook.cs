using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MacroFenetre.Services;

internal sealed class KeyboardHook : IDisposable
{
    private readonly NativeMethods.HookProc _callback;
    private nint _hookHandle;

    internal KeyboardHook(Func<int, bool, bool> handler)
    {
        _callback = (code, message, data) => HookCallback(code, message, data, handler);
        _hookHandle = NativeMethods.SetWindowsHookEx(
            NativeMethods.WhKeyboardLl,
            _callback,
            NativeMethods.GetModuleHandle(null),
            0);

        if (_hookHandle == nint.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Impossible d'installer le raccourci clavier global.");
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

    private nint HookCallback(int code, nint message, nint data, Func<int, bool, bool> handler)
    {
        if (code >= 0)
        {
            var isDown = message == NativeMethods.WmKeyDown || message == NativeMethods.WmSysKeyDown;
            var isUp = message == NativeMethods.WmKeyUp || message == NativeMethods.WmSysKeyUp;
            if (isDown || isUp)
            {
                var hookData = Marshal.PtrToStructure<NativeMethods.KeyboardHookData>(data);
                if ((hookData.Flags & NativeMethods.LlkhfInjected) == 0 &&
                    handler((int)hookData.VirtualKey, isDown))
                {
                    return 1;
                }
            }
        }

        return NativeMethods.CallNextHookEx(_hookHandle, code, message, data);
    }
}
