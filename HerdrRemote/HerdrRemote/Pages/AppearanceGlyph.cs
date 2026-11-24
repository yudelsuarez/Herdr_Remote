using System.Globalization;

namespace HerdrRemote.Pages;

// The Appearance segment's glyph (icons font) from its value: the sun for Light, the monitor for System, the moon for Dark; none for
// any other segment.
public sealed class AppearanceGlyph : IValueConverter
{
	public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
	{
		"Light" => "",
		"System" => "",
		"Dark" => "",
		_ => "",
	};

	public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
