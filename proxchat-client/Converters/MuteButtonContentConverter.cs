using System;
using System.Globalization;
using System.Windows.Data;

namespace ProxChatClient.Converters;

[ValueConversion(typeof(bool), typeof(string))]
public class MuteButtonContentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool isMuted)
        {
            return isMuted ? "Unmute" : "Mute";
        }
        return "Mute"; // Default or fallback
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        // Not needed for button content
        throw new NotSupportedException();
    }
} 