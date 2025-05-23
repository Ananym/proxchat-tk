using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace ProxChatClient.Services;

public class GlobalHotkeyService : IDisposable
{
    // Windows API declarations for low-level keyboard hook
    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    // Events for hotkey actions
    public event EventHandler<bool>? PushToTalkStateChanged; // bool = isPressed
    public event EventHandler? MuteToggleRequested;

    private readonly LowLevelKeyboardProc _proc;
    private IntPtr _hookID = IntPtr.Zero;
    private bool _isPushToTalkPressed = false;
    private readonly DebugLogService? _debugLog;

    // Hotkey configuration
    public Key PushToTalkKey { get; set; } = Key.F12;
    public Key MuteToggleKey { get; set; } = Key.F11;
    public bool IsPushToTalkEnabled { get; set; } = false;

    public GlobalHotkeyService(DebugLogService? debugLog = null)
    {
        _debugLog = debugLog;
        _proc = HookCallback; // Store instance method reference
    }

    public void StartHook()
    {
        if (_hookID != IntPtr.Zero)
        {
            _debugLog?.LogMain("Global hotkey hook already active");
            return;
        }

        _hookID = SetHook(_proc);
        if (_hookID != IntPtr.Zero)
        {
            _debugLog?.LogMain($"Global hotkey hook installed successfully. PTT: {PushToTalkKey}, Mute: {MuteToggleKey}");
        }
        else
        {
            _debugLog?.LogMain("Failed to install global hotkey hook");
        }
    }

    public void StopHook()
    {
        if (_hookID != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookID);
            _hookID = IntPtr.Zero;
            _isPushToTalkPressed = false; // Reset state
            _debugLog?.LogMain("Global hotkey hook uninstalled");
        }
    }

    public void UpdateHotkeys(Key pushToTalkKey, Key muteToggleKey, bool isPushToTalkEnabled)
    {
        PushToTalkKey = pushToTalkKey;
        MuteToggleKey = muteToggleKey;
        IsPushToTalkEnabled = isPushToTalkEnabled;
        
        _debugLog?.LogMain($"Hotkeys updated: PTT: {PushToTalkKey} (enabled: {IsPushToTalkEnabled}), Mute: {MuteToggleKey}");
    }

    private IntPtr SetHook(LowLevelKeyboardProc proc)
    {
        using (Process curProcess = Process.GetCurrentProcess())
        using (ProcessModule? curModule = curProcess.MainModule)
        {
            if (curModule != null)
            {
                return SetWindowsHookEx(WH_KEYBOARD_LL, proc,
                    GetModuleHandle(curModule.ModuleName), 0);
            }
        }
        return IntPtr.Zero;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            bool isKeyDown = wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN;
            bool isKeyUp = wParam == (IntPtr)WM_KEYUP || wParam == (IntPtr)WM_SYSKEYUP;

            if (isKeyDown || isKeyUp)
            {
                int vkCode = Marshal.ReadInt32(lParam);
                Key key = KeyInterop.KeyFromVirtualKey(vkCode);

                // Handle Push-to-Talk
                if (IsPushToTalkEnabled && key == PushToTalkKey)
                {
                    if (isKeyDown && !_isPushToTalkPressed)
                    {
                        _isPushToTalkPressed = true;
                        Application.Current.Dispatcher.BeginInvoke(() => 
                            PushToTalkStateChanged?.Invoke(this, true));
                    }
                    else if (isKeyUp && _isPushToTalkPressed)
                    {
                        _isPushToTalkPressed = false;
                        Application.Current.Dispatcher.BeginInvoke(() => 
                            PushToTalkStateChanged?.Invoke(this, false));
                    }
                }

                // Handle Mute Toggle (only on key down to avoid double-toggle)
                if (isKeyDown && key == MuteToggleKey)
                {
                    Application.Current.Dispatcher.BeginInvoke(() => 
                        MuteToggleRequested?.Invoke(this, EventArgs.Empty));
                }
            }
        }

        return CallNextHookEx(_hookID, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        StopHook();
        GC.SuppressFinalize(this);
    }
} 