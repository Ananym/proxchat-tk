using System;
using System.Globalization;
using System.Windows.Data;

namespace ProxChatClient.Converters;

[ValueConversion(typeof(bool), typeof(bool))]
public class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool booleanValue)
        {
            return !booleanValue;
        }
        return value; // Or Binding.DoNothing or throw exception
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        // ConvertBack is often not needed for one-way bindings like IsEnabled
        if (value is bool booleanValue)
        {
            return !booleanValue;
        }
        return value; // Or Binding.DoNothing or throw exception
        // throw new NotSupportedException();
    }
} 