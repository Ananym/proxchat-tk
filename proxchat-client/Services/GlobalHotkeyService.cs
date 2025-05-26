using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Collections.Generic;

namespace ProxChatClient.Services;

public class HotkeyDefinition
{
    public Key Key { get; set; } = Key.None;
    public bool Ctrl { get; set; } = false;
    public bool Shift { get; set; } = false;
    public bool Alt { get; set; } = false;
    public bool Win { get; set; } = false;

    public HotkeyDefinition() { }

    public HotkeyDefinition(Key key, bool ctrl = false, bool shift = false, bool alt = false, bool win = false)
    {
        Key = key;
        Ctrl = ctrl;
        Shift = shift;
        Alt = alt;
        Win = win;
    }

    public override string ToString()
    {
        if (Key == Key.None) return "None";
        
        var parts = new List<string>();
        if (Ctrl) parts.Add("Ctrl");
        if (Shift) parts.Add("Shift");
        if (Alt) parts.Add("Alt");
        if (Win) parts.Add("Win");
        parts.Add(Key.ToString());
        
        return string.Join("+", parts);
    }

    public static HotkeyDefinition FromString(string hotkeyString)
    {
        if (string.IsNullOrEmpty(hotkeyString) || hotkeyString == "None")
            return new HotkeyDefinition();

        // Handle JSON escaped plus sign
        string normalizedString = hotkeyString.Replace("\\u002B", "+").Replace("\u002B", "+");
        
        var parts = normalizedString.Split('+');
        var hotkey = new HotkeyDefinition();
        
        foreach (var part in parts)
        {
            switch (part.Trim().ToLower())
            {
                case "ctrl":
                    hotkey.Ctrl = true;
                    break;
                case "shift":
                    hotkey.Shift = true;
                    break;
                case "alt":
                    hotkey.Alt = true;
                    break;
                case "win":
                    hotkey.Win = true;
                    break;
                default:
                    if (Enum.TryParse<Key>(part.Trim(), out Key key))
                        hotkey.Key = key;
                    break;
            }
        }
        
        return hotkey;
    }

    public static HotkeyDefinition FromStringWithDefault(string hotkeyString, Key defaultKey)
    {
        try
        {
            var hotkey = FromString(hotkeyString);
            return hotkey.Key != Key.None ? hotkey : new HotkeyDefinition(defaultKey);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HOTKEY] Parse error: {ex.Message}, falling back to default {defaultKey}");
            return new HotkeyDefinition(defaultKey);
        }
    }

    public bool Matches(Key pressedKey, bool ctrlPressed, bool shiftPressed, bool altPressed, bool winPressed)
    {
        return Key == pressedKey &&
               Ctrl == ctrlPressed &&
               Shift == shiftPressed &&
               Alt == altPressed &&
               Win == winPressed;
    }
}

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

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    // Virtual key codes for modifiers
    private const int VK_CONTROL = 0x11;
    private const int VK_SHIFT = 0x10;
    private const int VK_MENU = 0x12; // Alt key
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;

    // Events for hotkey actions
    public event EventHandler<bool>? PushToTalkStateChanged; // bool = isPressed
    public event EventHandler? MuteToggleRequested;

    private readonly LowLevelKeyboardProc _proc;
    private IntPtr _hookID = IntPtr.Zero;
    private bool _isPushToTalkPressed = false;
    private readonly DebugLogService? _debugLog;

    // Hotkey configuration
    public HotkeyDefinition PushToTalkHotkey { get; set; } = new HotkeyDefinition(Key.OemBackslash);
    public HotkeyDefinition MuteToggleHotkey { get; set; } = new HotkeyDefinition(Key.M, ctrl: true);
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
            _debugLog?.LogMain($"Global hotkey hook installed successfully. PTT: {PushToTalkHotkey}, Mute: {MuteToggleHotkey}");
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

    public void UpdateHotkeys(HotkeyDefinition pushToTalkHotkey, HotkeyDefinition muteToggleHotkey, bool isPushToTalkEnabled)
    {
        PushToTalkHotkey = pushToTalkHotkey;
        MuteToggleHotkey = muteToggleHotkey;
        IsPushToTalkEnabled = isPushToTalkEnabled;
        
        _debugLog?.LogMain($"Hotkeys updated: PTT: {PushToTalkHotkey}, Mute: {MuteToggleHotkey}");
    }

    // Helper methods for config serialization and backwards compatibility
    public static string KeyToString(Key key)
    {
        return new HotkeyDefinition(key).ToString();
    }

    public static Key StringToKey(string keyString)
    {
        var hotkey = HotkeyDefinition.FromString(keyString);
        return hotkey.Key;
    }

    public static Key StringToKeyWithDefault(string keyString, Key defaultKey)
    {
        try
        {
            var hotkey = HotkeyDefinition.FromString(keyString);
            return hotkey.Key != Key.None ? hotkey.Key : defaultKey;
        }
        catch
        {
            return defaultKey;
        }
    }

    public static HotkeyDefinition StringToHotkeyWithDefault(string hotkeyString, HotkeyDefinition defaultHotkey)
    {
        try
        {
            var hotkey = HotkeyDefinition.FromString(hotkeyString);
            return hotkey.Key != Key.None ? hotkey : defaultHotkey;
        }
        catch
        {
            return defaultHotkey;
        }
    }

    private bool IsModifierPressed(int vkCode)
    {
        return (GetKeyState(vkCode) & 0x8000) != 0;
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

                // Check modifier states
                bool ctrlPressed = IsModifierPressed(VK_CONTROL);
                bool shiftPressed = IsModifierPressed(VK_SHIFT);
                bool altPressed = IsModifierPressed(VK_MENU);
                bool winPressed = IsModifierPressed(VK_LWIN) || IsModifierPressed(VK_RWIN);

                // Handle Push-to-Talk
                if (IsPushToTalkEnabled && PushToTalkHotkey.Matches(key, ctrlPressed, shiftPressed, altPressed, winPressed))
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
                if (isKeyDown && MuteToggleHotkey.Matches(key, ctrlPressed, shiftPressed, altPressed, winPressed))
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