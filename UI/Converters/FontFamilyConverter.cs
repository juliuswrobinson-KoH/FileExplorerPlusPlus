using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace UI.Converters;

public sealed class FontFamilyConverter : IValueConverter
{
    private static readonly Dictionary<string, FontFamily> FontFamilies = new(StringComparer.OrdinalIgnoreCase);

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var name = value as string;
        if (string.IsNullOrWhiteSpace(name))
            name = "Segoe UI";

        lock (FontFamilies)
        {
            if (!FontFamilies.TryGetValue(name, out var fontFamily))
            {
                fontFamily = new FontFamily(name);
                FontFamilies.Add(name, fontFamily);
            }

            return fontFamily;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
