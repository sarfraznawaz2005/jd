using System.Globalization;
using Avalonia;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace JustDownload.App.Converters;

/// <summary>
/// Resolves a string resource key to the application resource it names (TASK-050) — e.g. a sidebar node's
/// icon-geometry key to its <c>StreamGeometry</c>. Lets view-models reference resources by key without taking
/// a dependency on Avalonia types.
/// </summary>
public sealed class ResourceKeyConverter : IValueConverter
{
    /// <summary>Singleton for XAML use without per-binding allocation.</summary>
    public static ResourceKeyConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string key || key.Length == 0)
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
