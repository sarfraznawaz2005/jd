using System.Globalization;
using Avalonia;
using Avalonia.Data;
using Avalonia.Data.Converters;
using JustDownload.Core.Categorization;

namespace JustDownload.App.Converters;

/// <summary>
/// Resolves a <see cref="FileCategory"/> to one of its visual resources (icon geometry, foreground tint, or
/// background tint) by looking the mapped key up in the application's resource dictionaries (TASK-051). The
/// aspect is chosen via the converter parameter: <c>geometry</c>, <c>foreground</c>, or <c>background</c>.
/// Category tints are theme-independent fixed colours, so a one-time lookup is correct.
/// </summary>
public sealed class CategoryResourceConverter : IValueConverter
{
    /// <summary>Singleton for XAML use without per-binding allocation.</summary>
    public static CategoryResourceConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not FileCategory category || parameter is not string aspect)
        {
            return BindingOperations.DoNothing;
        }

        string key = aspect switch
        {
            "geometry" => CategoryVisuals.GeometryKey(category),
            "foreground" => CategoryVisuals.ForegroundKey(category),
            "background" => CategoryVisuals.BackgroundKey(category),
            _ => string.Empty,
        };

        if (key.Length == 0)
        {
            return BindingOperations.DoNothing;
        }

        return Application.Current?.TryGetResource(key, null, out object? resource) == true
            ? resource
            : BindingOperations.DoNothing;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
