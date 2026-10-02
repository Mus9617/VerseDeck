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
}
