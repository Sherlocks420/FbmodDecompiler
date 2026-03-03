using System;
using System.Globalization;
using System.Windows.Data;

namespace FbmodDecompiler
{

    public sealed class ProgressWidthConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values == null || values.Length < 3)
                return 0d;

            double actualWidth = ToDouble(values[0]);
            double value = ToDouble(values[1]);
            double maximum = ToDouble(values[2]);

            if (actualWidth <= 0 || maximum <= 0)
                return 0d;

            if (value < 0) value = 0;
            if (value > maximum) value = maximum;

            double ratio = value / maximum;
            double width = actualWidth * ratio;

            if (width < 0) width = 0;
            if (width > actualWidth) width = actualWidth;

            return width;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotSupportedException();

        private static double ToDouble(object v)
        {
            try
            {
                if (v == null) return 0d;
                if (v is double d) return d;
                if (v is float f) return f;
                if (v is int i) return i;
                if (v is long l) return l;
                if (double.TryParse(v.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out double parsed))
                    return parsed;
            }
            catch { }
            return 0d;
        }
    }
}
