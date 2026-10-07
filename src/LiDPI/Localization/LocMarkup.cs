using System.Globalization;
using System.Windows.Data;
using System.Windows.Markup;
using LiDPI.Core;

namespace LiDPI.Localization;

/// <summary>
/// <c>{l:T Key}</c>: a one-way binding to <see cref="Loc"/>'s indexer, so the text follows a language switch.
/// Works on dependency properties and in style setters.
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class TExtension : MarkupExtension
{
    public TExtension() { }

    public TExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Key}]") { Source = Loc.Instance, Mode = BindingMode.OneWay }.ProvideValue(serviceProvider);
}

/// <summary>
/// Multi-binding converter for items whose text comes from <see cref="Loc"/>: values[0] is the item, values[1] is
/// <see cref="Loc.Version"/> (only there to re-run the conversion after a language switch).
/// A <see cref="BypassProfile"/> gives its localized name, or its description with ConverterParameter "desc";
/// anything else gives ToString().
/// </summary>
public sealed class LiveTextConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values.Length == 0 ? "" : values[0] switch
        {
            BypassProfile p => parameter as string == "desc" ? p.DisplayDescription : p.DisplayName,
            null => "",
            var o => o.ToString() ?? "",
        };

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
