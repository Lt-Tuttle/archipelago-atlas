using System.Text.RegularExpressions;
using AP_Atlas.Core;
using Xunit;

namespace AP_Atlas.Core.Tests;

public class WordmarkSvgTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_templates_colours_are_swapped_for_the_themes(bool dark)
    {
        string svg = WordmarkSvg.For(dark, "#111111", "#222222", "#333333");
        Assert.StartsWith("<svg", svg);
        Assert.Contains("fill=\"#111111\"", svg);            // the lettering
        Assert.Contains("stroke=\"#222222\"", svg);          // the lines
        Assert.Equal(5, Regex.Matches(svg, "fill=\"#333333\"").Count); // the bar's five filled segments
        foreach (string key in new[] { "#F3EFE6", "#A9A3B8", "#B98AF0", "#201B33", "#6B6480", "#8A2BE2" })
            Assert.DoesNotContain(key, svg, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\u0001", svg, System.StringComparison.Ordinal); // a culture-aware comparison ignores control characters
    }

    [Fact]
    public void A_theme_colour_equal_to_another_key_is_not_swapped_twice()
    {
        // The light template's accent key is the default accent; asking for the text in that colour must not recolour the bar.
        string svg = WordmarkSvg.For(false, "#8A2BE2", "#222222", "#333333");
        Assert.Equal(5, Regex.Matches(svg, "fill=\"#333333\"").Count);
        Assert.Contains("fill=\"#8A2BE2\"", svg);
    }
}
