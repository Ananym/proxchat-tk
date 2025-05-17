using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace ProxChatClient.Converters
{
    public class InputLevelThresholdColorConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length == 2 &&
                values[0] is float audioLevel &&
                values[1] is float threshold)
            {
                if (audioLevel < threshold)
                    return Brushes.Orange;
                else
                    return Brushes.DodgerBlue; // Default WPF blue
            }
            return Brushes.DodgerBlue;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
} 