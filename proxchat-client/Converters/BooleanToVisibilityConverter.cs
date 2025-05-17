using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ProxChatClient.Converters;

[ValueConversion(typeof(bool), typeof(Visibility))]
public class BooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool boolValue = false;
        if (value is bool b)
        {
            boolValue = b;
        }

        // Optional: Check parameter to invert logic (e.g., parameter="invert")
        bool invert = parameter as string == "invert";
        if (invert)
        {
            boolValue = !boolValue;
        }

        return boolValue ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is Visibility visibility)
        {
            return visibility == Visibility.Visible;
        }
        return false;
    }
} 