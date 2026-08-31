using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Pinboard.App.Services;

/// <summary>
/// Observes PixPin's normal Ctrl+Alt+A shortcut without registering or consuming it.
/// PixPin remains the owner of the shortcut; this listener merely arms Pinboard to
/// accept the next image copied by that screenshot flow.
/// </summary>
public sealed class PixPinDefaultShortcutListener : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const int VkA = 0x41;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;

    private readonly LowLevelKeyboardProc _callback;
    private IntPtr _hook;
    private bool _aKeyIsDown;

    public PixPinDefaultShortcutListener()
    {
        _callback = HookCallback;
    }

    public event EventHandler? ShortcutPressed;

    public bool IsListening => _hook != IntPtr.Zero;

    public void Start()
    {
        if (IsListening)
        {
            return;
        }

        _hook = SetWindowsHookEx(WhKeyboardLl, _callback, IntPtr.Zero, 0);
        if (_hook == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    private IntPtr HookCallback(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0)
        {
            var virtualKey = Marshal.ReadInt32(data);
            var windowMessage = message.ToInt32();
            if (virtualKey == VkA && (windowMessage == WmKeyDown || windowMessage == WmSysKeyDown))
            {
                var controlDown = (GetAsyncKeyState(VkControl) & 0x8000) != 0;
                var altDown = (GetAsyncKeyState(VkMenu) & 0x8000) != 0;
                if (controlDown && altDown && !_aKeyIsDown)
                {
                    _aKeyIsDown = true;
                    ShortcutPressed?.Invoke(this, EventArgs.Empty);
                }
            }
            else if (virtualKey == VkA && (windowMessage == WmKeyUp || windowMessage == WmSysKeyUp))
            {
                _aKeyIsDown = false;
            }
        }

        return CallNextHookEx(_hook, code, message, data);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
        GC.SuppressFinalize(this);
    }

    private delegate IntPtr LowLevelKeyboardProc(int code, IntPtr message, IntPtr data);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
}
