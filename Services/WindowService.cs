using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using MacroFenetre.Models;

namespace MacroFenetre.Services;

internal static class WindowService
{
    internal static IReadOnlyList<WindowItem> EnumerateVisibleWindows(nint excludedHandle)
    {
        var windows = new List<WindowItem>();

        NativeMethods.EnumWindows((handle, _) =>
        {
            if (handle == excludedHandle || !NativeMethods.IsWindowVisible(handle))
            {
                return true;
            }

            var titleLength = NativeMethods.GetWindowTextLength(handle);
            if (titleLength == 0 || IsCloaked(handle))
            {
                return true;
            }

            var titleBuilder = new StringBuilder(titleLength + 1);
            NativeMethods.GetWindowText(handle, titleBuilder, titleBuilder.Capacity);
            var title = titleBuilder.ToString().Trim();
            if (string.IsNullOrWhiteSpace(title))
            {
                return true;
            }

            try
            {
                NativeMethods.GetWindowThreadProcessId(handle, out var processId);
                using var process = Process.GetProcessById((int)processId);
                windows.Add(new WindowItem
                {
                    Handle = handle,
                    Title = title,
                    ProcessName = FriendlyProcessName(process.ProcessName)
                });
            }
            catch (ArgumentException)
            {
                // The window closed between enumeration and process lookup.
            }
            catch (InvalidOperationException)
            {
                // The process exited while its data was being read.
            }

            return true;
        }, nint.Zero);

        return windows
            .OrderBy(window => window.ProcessName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(window => window.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    internal static bool TryGetClientArea(nint handle, out NativeMethods.Point origin, out int width, out int height)
    {
        origin = new NativeMethods.Point();
        width = 0;
        height = 0;

        if (!NativeMethods.IsWindow(handle) ||
            !NativeMethods.GetClientRect(handle, out var rectangle) ||
            rectangle.Width <= 0 ||
            rectangle.Height <= 0 ||
            !NativeMethods.ClientToScreen(handle, ref origin))
        {
            return false;
        }

        width = rectangle.Width;
        height = rectangle.Height;
        return true;
    }

    internal static bool IsPointInClientArea(nint handle, NativeMethods.Point point)
    {
        return TryGetClientArea(handle, out var origin, out var width, out var height) &&
               point.X >= origin.X && point.X < origin.X + width &&
               point.Y >= origin.Y && point.Y < origin.Y + height;
    }

    internal static bool ActivateWindow(nint handle)
    {
        if (!NativeMethods.IsWindow(handle))
        {
            return false;
        }

        NativeMethods.ShowWindow(handle, NativeMethods.SwRestore);
        if (NativeMethods.SetForegroundWindow(handle))
        {
            return true;
        }

        var targetThread = NativeMethods.GetWindowThreadProcessId(handle, out _);
        var foregroundHandle = NativeMethods.GetForegroundWindow();
        var foregroundThread = foregroundHandle == nint.Zero
            ? 0
            : NativeMethods.GetWindowThreadProcessId(foregroundHandle, out _);
        var currentThread = NativeMethods.GetCurrentThreadId();

        var attachedToTarget = targetThread != 0 && targetThread != currentThread &&
                               NativeMethods.AttachThreadInput(currentThread, targetThread, true);
        var attachedToForeground = foregroundThread != 0 && foregroundThread != currentThread &&
                                   foregroundThread != targetThread &&
                                   NativeMethods.AttachThreadInput(currentThread, foregroundThread, true);

        try
        {
            NativeMethods.BringWindowToTop(handle);
            return NativeMethods.SetForegroundWindow(handle);
        }
        finally
        {
            if (attachedToForeground)
            {
                NativeMethods.AttachThreadInput(currentThread, foregroundThread, false);
            }

            if (attachedToTarget)
            {
                NativeMethods.AttachThreadInput(currentThread, targetThread, false);
            }
        }
    }

    internal static nint RootWindowAt(NativeMethods.Point point)
    {
        var handle = NativeMethods.WindowFromPoint(point);
        return handle == nint.Zero ? nint.Zero : NativeMethods.GetAncestor(handle, NativeMethods.GaRoot);
    }

    private static bool IsCloaked(nint handle)
    {
        try
        {
            return NativeMethods.DwmGetWindowAttribute(
                       handle,
                       NativeMethods.DwmwaCloaked,
                       out var cloaked,
                       Marshal.SizeOf<int>()) == 0 && cloaked != 0;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
    }

    private static string FriendlyProcessName(string processName)
    {
        if (processName.Equals("WINWORD", StringComparison.OrdinalIgnoreCase))
        {
            return "Word";
        }

        if (processName.Equals("EXCEL", StringComparison.OrdinalIgnoreCase))
        {
            return "Excel";
        }

        if (processName.Equals("POWERPNT", StringComparison.OrdinalIgnoreCase))
        {
            return "PowerPoint";
        }

        return char.ToUpperInvariant(processName[0]) + processName[1..];
    }
}
