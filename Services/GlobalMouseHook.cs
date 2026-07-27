using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MacroFenetre.Services;

internal sealed class GlobalMouseHook : IDisposable
{
    internal const int MiddleButton = 1;
    internal const int SideButton1 = 2;
    internal const int SideButton2 = 3;

    private readonly NativeMethods.HookProc _callback;
    private nint _hookHandle;

    internal GlobalMouseHook(Func<int, bool, bool> handler)
    {
        _callback = (code, message, data) => HookCallback(code, message, data, handler);
        _hookHandle = NativeMethods.SetWindowsHookEx(
            NativeMethods.WhMouseLl,
            _callback,
            NativeMethods.GetModuleHandle(null),
            0);

        if (_hookHandle == nint.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Impossible d’installer les raccourcis de souris globaux.");
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

    internal static string GetButtonName(int buttonCode) => buttonCode switch
    {
        MiddleButton => "Bouton du milieu",
        SideButton1 => "Bouton latéral 1",
        SideButton2 => "Bouton latéral 2",
        _ => "Bouton souris"
    };

    private nint HookCallback(int code, nint message, nint data, Func<int, bool, bool> handler)
    {
        if (code >= 0 && TryGetButton(message, data, out var buttonCode, out var isDown))
        {
            var hookData = Marshal.PtrToStructure<NativeMethods.MouseHookData>(data);
            if ((hookData.Flags & NativeMethods.LlmhfInjected) == 0 &&
                handler(buttonCode, isDown))
            {
                return 1;
            }
        }

        return NativeMethods.CallNextHookEx(_hookHandle, code, message, data);
    }

    private static bool TryGetButton(nint message, nint data, out int buttonCode, out bool isDown)
    {
        if (message == NativeMethods.WmMButtonDown || message == NativeMethods.WmMButtonUp)
        {
            buttonCode = MiddleButton;
            isDown = message == NativeMethods.WmMButtonDown;
            return true;
        }

        if (message == NativeMethods.WmXButtonDown || message == NativeMethods.WmXButtonUp)
        {
            var hookData = Marshal.PtrToStructure<NativeMethods.MouseHookData>(data);
            var xButton = (hookData.MouseData >> 16) & 0xFFFF;
            buttonCode = xButton == 1 ? SideButton1 : SideButton2;
            isDown = message == NativeMethods.WmXButtonDown;
            return true;
        }

        buttonCode = 0;
        isDown = false;
        return false;
    }
}
