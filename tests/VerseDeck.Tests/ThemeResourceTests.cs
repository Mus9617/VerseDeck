using System.IO.Packaging;
using System.Windows;
using System.Windows.Media;
using VerseDeck.App.Services;

namespace VerseDeck.Tests;

public class ThemeResourceTests
{
    private static readonly string[] BrushKeys = ["Bg", "Surface", "SurfaceRaised", "Line", "Text", "TextMuted", "Accent", "OnAccent", "Positive", "Danger", "Warning"];
    private static readonly string[] FontKeys = ["HeadingFont", "BodyFont", "MonoFont"];

    public static IEnumerable<object[]> Themes => Enum.GetNames<ThemeId>().Select(name => new object[] { name });

    private static T OnSta<T>(Func<T> work)
    {
        T result = default!;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new InvalidOperationException(failure.Message, failure);
        }

        return result;
    }

    private static ResourceDictionary Load(string name)
    {
        // Touching these types registers the pack:// scheme outside a running application.
        _ = Application.Current;
        _ = PackUriHelper.UriSchemePack;
        return (ResourceDictionary)Application.LoadComponent(new Uri($"/VerseDeck.App;component/Themes/{name}.xaml", UriKind.Relative));
    }

    private static double Luminance(Color color)
    {
        static double Channel(byte value)
        {
            var c = value / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
    }

    private static double Contrast(ResourceDictionary theme, string foreground, string background)
    {
        var a = Luminance(((SolidColorBrush)theme[foreground]).Color);
        var b = Luminance(((SolidColorBrush)theme[background]).Color);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void Theme_DefinesEveryToken(string theme)
    {
        var missing = OnSta(() =>
        {
            var dictionary = Load(theme);
            var problems = new List<string>();
            problems.AddRange(BrushKeys.Where(k => dictionary[k] is not SolidColorBrush));
            problems.AddRange(FontKeys.Where(k => dictionary[k] is not FontFamily));
            if (dictionary["Radius"] is not CornerRadius)
            {
                problems.Add("Radius");
            }

            if (dictionary["Stroke"] is not Thickness)
            {
                problems.Add("Stroke");
            }

            return problems;
        });

        Assert.Empty(missing);
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void Theme_TextIsReadable(string theme)
    {
        var ratios = OnSta(() =>
        {
            var dictionary = Load(theme);
            return new Dictionary<string, double>
            {
                ["Text/Bg"] = Contrast(dictionary, "Text", "Bg"),
                ["Text/SurfaceRaised"] = Contrast(dictionary, "Text", "SurfaceRaised"),
                ["TextMuted/Bg"] = Contrast(dictionary, "TextMuted", "Bg"),
                ["TextMuted/SurfaceRaised"] = Contrast(dictionary, "TextMuted", "SurfaceRaised"),
                ["OnAccent/Accent"] = Contrast(dictionary, "OnAccent", "Accent"),
                ["Danger/Bg"] = Contrast(dictionary, "Danger", "Bg"),
                ["Accent/Surface"] = Contrast(dictionary, "Accent", "Surface")
            };
        });

        Assert.True(ratios["Text/Bg"] >= 7, $"Text/Bg {ratios["Text/Bg"]:0.0}");
        Assert.True(ratios["Text/SurfaceRaised"] >= 7, $"Text/SurfaceRaised {ratios["Text/SurfaceRaised"]:0.0}");
        Assert.True(ratios["TextMuted/Bg"] >= 4.5, $"TextMuted/Bg {ratios["TextMuted/Bg"]:0.0}");
        Assert.True(ratios["TextMuted/SurfaceRaised"] >= 4.5, $"TextMuted/SurfaceRaised {ratios["TextMuted/SurfaceRaised"]:0.0}");
        Assert.True(ratios["OnAccent/Accent"] >= 4.5, $"OnAccent/Accent {ratios["OnAccent/Accent"]:0.0}");
        Assert.True(ratios["Danger/Bg"] >= 4.5, $"Danger/Bg {ratios["Danger/Bg"]:0.0}");
        Assert.True(ratios["Accent/Surface"] >= 3, $"Accent/Surface {ratios["Accent/Surface"]:0.0}");
    }

    [Fact]
    public void Icons_DefineEveryModuleAndNavigationKey()
    {
        var missing = OnSta(() =>
        {
            var icons = Load("Icons");
            return ModuleStyle.Icons
                .Concat(["nav.deck", "nav.controls", "nav.copilot", "nav.aboard", "nav.voice", "nav.mobile", "nav.settings"])
                .Where(key => icons[$"Icon.{key}"] is not Geometry)
                .ToList();
        });

        Assert.Empty(missing);
    }

    [Fact]
    public void Controls_Load()
    {
        var count = OnSta(() => Load("Controls").Count);

        Assert.True(count > 0);
    }
}
