using System.Globalization;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;

namespace RefereeHubApi.Desktop.Tests;

// the palette fonts ship in Assets/Fonts; each weight the views use must resolve to the bundled
// face, not fall through to Inter or a system font.
public sealed class FontTests
{
    [AvaloniaTheory]
    [InlineData("UiFont", "Outfit", FontWeight.Normal)]
    [InlineData("UiFont", "Outfit", FontWeight.Medium)]
    [InlineData("UiFont", "Outfit", FontWeight.SemiBold)]
    [InlineData("UiFont", "Outfit", FontWeight.Bold)]
    [InlineData("UiFont", "Outfit", FontWeight.ExtraBold)]
    [InlineData("DisplayFont", "Quicksand", FontWeight.Light)]
    [InlineData("DisplayFont", "Quicksand", FontWeight.Normal)]
    [InlineData("DisplayFont", "Quicksand", FontWeight.Medium)]
    [InlineData("DisplayFont", "Quicksand", FontWeight.Bold)]
    [InlineData("MonoFont", "JetBrains Mono", FontWeight.Normal)]
    [InlineData("MonoFont", "JetBrains Mono", FontWeight.Bold)]
    public void BundledFontResolves(string key, string family, FontWeight weight)
    {
        Assert.True(Application.Current!.TryGetResource(key, null, out var resource));
        var typeface = new Typeface((FontFamily)resource!, FontStyle.Normal, weight);

        Assert.True(FontManager.Current.TryGetGlyphTypeface(typeface, out var glyphs));
        Assert.StartsWith(family, glyphs.FamilyName);
        Assert.Equal(weight, glyphs.Weight);
    }

    // Outfit and Quicksand have no Cyrillic or Greek; names in those scripts must fall back to
    // Inter, not to another bundled family (JetBrains Mono has both).
    [AvaloniaTheory]
    [InlineData("UiFont", "Ж")]
    [InlineData("UiFont", "Ω")]
    [InlineData("DisplayFont", "Ж")]
    public void MissingGlyphsFallBackToInter(string key, string text)
    {
        Assert.True(Application.Current!.TryGetResource(key, null, out var resource));

        Assert.True(FontManager.Current.TryMatchCharacter(char.ConvertToUtf32(text, 0), FontStyle.Normal, FontWeight.Normal,
            FontStretch.Normal, (FontFamily)resource!, CultureInfo.InvariantCulture, out var typeface));
        Assert.StartsWith("Inter", typeface.GlyphTypeface.FamilyName);
    }
}
