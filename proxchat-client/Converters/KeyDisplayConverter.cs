using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Input;

namespace ProxChatClient.Converters;

[ValueConversion(typeof(string), typeof(string))]
public class KeyDisplayConverter : IValueConverter
{
    private static readonly Dictionary<Key, string> KeyDisplayMap = CreateKeyDisplayMap();

    private static Dictionary<Key, string> CreateKeyDisplayMap()
    {
        var map = new Dictionary<Key, string>();
        
        // use TryAdd to avoid duplicate key exceptions
        // common oem keys that have visual characters
        map.TryAdd(Key.Oem5, "\\"); // backslash key on most US keyboards
        map.TryAdd(Key.OemBackslash, "\\"); // alternative name for same key
        map.TryAdd(Key.OemPipe, "|");
        map.TryAdd(Key.OemQuestion, "?");
        map.TryAdd(Key.OemPeriod, ".");
        map.TryAdd(Key.OemComma, ",");
        map.TryAdd(Key.OemSemicolon, ";");
        map.TryAdd(Key.OemQuotes, "'");
        map.TryAdd(Key.OemOpenBrackets, "[");
        map.TryAdd(Key.OemCloseBrackets, "]");
        map.TryAdd(Key.OemMinus, "-");
        map.TryAdd(Key.OemPlus, "+");
        map.TryAdd(Key.OemTilde, "~");
        
        // number row symbols
        map.TryAdd(Key.D1, "1"); map.TryAdd(Key.D2, "2"); map.TryAdd(Key.D3, "3"); 
        map.TryAdd(Key.D4, "4"); map.TryAdd(Key.D5, "5"); map.TryAdd(Key.D6, "6"); 
        map.TryAdd(Key.D7, "7"); map.TryAdd(Key.D8, "8"); map.TryAdd(Key.D9, "9"); 
        map.TryAdd(Key.D0, "0");
        
        // function keys
        map.TryAdd(Key.F1, "F1"); map.TryAdd(Key.F2, "F2"); map.TryAdd(Key.F3, "F3"); 
        map.TryAdd(Key.F4, "F4"); map.TryAdd(Key.F5, "F5"); map.TryAdd(Key.F6, "F6"); 
        map.TryAdd(Key.F7, "F7"); map.TryAdd(Key.F8, "F8"); map.TryAdd(Key.F9, "F9"); 
        map.TryAdd(Key.F10, "F10"); map.TryAdd(Key.F11, "F11"); map.TryAdd(Key.F12, "F12");
        
        // special keys
        map.TryAdd(Key.Space, "Space");
        map.TryAdd(Key.Enter, "Enter");
        map.TryAdd(Key.Tab, "Tab");
        map.TryAdd(Key.Escape, "Esc");
        map.TryAdd(Key.Back, "Backspace");
        map.TryAdd(Key.Delete, "Delete");
        map.TryAdd(Key.Insert, "Insert");
        map.TryAdd(Key.Home, "Home");
        map.TryAdd(Key.End, "End");
        map.TryAdd(Key.PageUp, "Page Up");
        map.TryAdd(Key.PageDown, "Page Down");
        
        // arrow keys
        map.TryAdd(Key.Up, "↑");
        map.TryAdd(Key.Down, "↓");
        map.TryAdd(Key.Left, "←");
        map.TryAdd(Key.Right, "→");
        
        // numpad
        map.TryAdd(Key.NumPad0, "Num 0"); map.TryAdd(Key.NumPad1, "Num 1"); 
        map.TryAdd(Key.NumPad2, "Num 2"); map.TryAdd(Key.NumPad3, "Num 3"); 
        map.TryAdd(Key.NumPad4, "Num 4"); map.TryAdd(Key.NumPad5, "Num 5"); 
        map.TryAdd(Key.NumPad6, "Num 6"); map.TryAdd(Key.NumPad7, "Num 7"); 
        map.TryAdd(Key.NumPad8, "Num 8"); map.TryAdd(Key.NumPad9, "Num 9");
        map.TryAdd(Key.Multiply, "Num *"); map.TryAdd(Key.Add, "Num +"); 
        map.TryAdd(Key.Subtract, "Num -"); map.TryAdd(Key.Divide, "Num /"); 
        map.TryAdd(Key.Decimal, "Num .");
        
        return map;
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string hotkeyString && !string.IsNullOrEmpty(hotkeyString))
        {
            // parse the hotkey string to extract individual parts
            var parts = hotkeyString.Split('+');
            var convertedParts = new List<string>();
            
            foreach (var part in parts)
            {
                var trimmedPart = part.Trim();
                
                // try to parse as a Key enum value
                if (Enum.TryParse<Key>(trimmedPart, out Key key))
                {
                    // use mapped display name if available, otherwise use the key name as-is
                    if (KeyDisplayMap.TryGetValue(key, out string? displayName))
                    {
                        convertedParts.Add(displayName);
                    }
                    else
                    {
                        // for regular letter keys, just use the letter
                        if (key >= Key.A && key <= Key.Z)
                        {
                            convertedParts.Add(key.ToString());
                        }
                        else
                        {
                            // fallback to original key name
                            convertedParts.Add(trimmedPart);
                        }
                    }
                }
                else
                {
                    // not a key (probably a modifier like Ctrl, Shift, etc.)
                    convertedParts.Add(trimmedPart);
                }
            }
            
            return string.Join(" + ", convertedParts);
        }
        
        return value?.ToString() ?? "None";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        // conversion back not needed for display purposes
        throw new NotSupportedException("KeyDisplayConverter is for display only");
    }
} 