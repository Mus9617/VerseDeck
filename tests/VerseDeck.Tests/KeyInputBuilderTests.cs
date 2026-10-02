using VerseDeck.Core.Models;
using VerseDeck.Input;

namespace VerseDeck.Tests;

public class KeyInputBuilderTests
{
    private const ushort Alt = 0x12;
    private const ushort Ctrl = 0x11;
    private const ushort R = 0x52;

    private static ushort FakeScanCode(ushort virtualKey) => (ushort)(virtualKey + 0x100);

    [Fact]
    public void Down_OrdersModifiersThenKey()
    {
        var strokes = KeyInputBuilder.Down(new KeyPressAction("R", ["Alt"], 60), FakeScanCode);

        Assert.Equal([Alt, R], strokes.Select(s => s.VirtualKey));
        Assert.All(strokes, s => Assert.False(s.KeyUp));
        Assert.Equal(FakeScanCode(R), strokes[1].ScanCode);
    }

    [Fact]
    public void Up_ReleasesKeyThenModifiersReversed()
    {
        var strokes = KeyInputBuilder.Up(new KeyPressAction("R", ["Ctrl", "Alt"], 60), FakeScanCode);

        Assert.Equal([R, Alt, Ctrl], strokes.Select(s => s.VirtualKey));
        Assert.All(strokes, s => Assert.True(s.KeyUp));
    }

    [Theory]
    [InlineData("LEFT")]
    [InlineData("UP")]
    [InlineData("INSERT")]
    [InlineData("DELETE")]
    [InlineData("HOME")]
    [InlineData("END")]
    public void NavigationKeys_AreExtended(string key)
    {
        var strokes = KeyInputBuilder.Down(new KeyPressAction(key, [], 60), FakeScanCode);

        Assert.True(strokes.Single().Extended);
    }

    [Fact]
    public void LetterKey_IsNotExtended()
    {
        var strokes = KeyInputBuilder.Down(new KeyPressAction("N", [], 60), FakeScanCode);

        Assert.False(strokes.Single().Extended);
    }

    [Fact]
    public void UnsupportedKey_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => KeyInputBuilder.Down(new KeyPressAction("ñ", [], 60), FakeScanCode));
    }

    [Theory]
    [InlineData("RAlt", 0xA5, true)]
    [InlineData("RCtrl", 0xA3, true)]
    [InlineData("LAlt", 0xA4, false)]
    [InlineData("LShift", 0xA0, false)]
    public void SidedModifier_MapsToItsOwnKey(string modifier, int virtualKey, bool extended)
    {
        var stroke = KeyInputBuilder.Down(new KeyPressAction("R", [modifier], 60), FakeScanCode)[0];

        Assert.Equal((ushort)virtualKey, stroke.VirtualKey);
        Assert.Equal(extended, stroke.Extended);
    }

    [Fact]
    public void PunctuationKey_UsesFixedScanCode_NotTheLayout()
    {
        var asked = new List<ushort>();
        var stroke = KeyInputBuilder.Down(new KeyPressAction("COMMA", [], 60), vk => { asked.Add(vk); return 0x99; }).Single();

        Assert.Equal(0, stroke.VirtualKey);
        Assert.Equal(0x33, stroke.ScanCode);
        Assert.Empty(asked);
    }

    [Fact]
    public void NumpadDivide_IsExtended()
    {
        Assert.True(KeyInputBuilder.Down(new KeyPressAction("NP_DIVIDE", [], 60), FakeScanCode).Single().Extended);
    }

    [Theory]
    [InlineData("COMMA")]
    [InlineData("backslash")]
    [InlineData("NP_5")]
    [InlineData("PGUP")]
    [InlineData("RALT")]
    [InlineData("capslock")]
    public void IsSupported_NewKeys(string key)
    {
        Assert.True(KeyMap.IsSupported(key));
    }

    [Theory]
    [InlineData("ñ")]
    [InlineData("F99")]
    [InlineData("")]
    [InlineData("mouse5")]
    public void IsSupported_RejectsUnknownKeys(string key)
    {
        Assert.False(KeyMap.IsSupported(key));
    }
}
