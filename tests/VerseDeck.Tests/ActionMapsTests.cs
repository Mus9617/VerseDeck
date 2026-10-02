using VerseDeck.Game;

namespace VerseDeck.Tests;

public static class Fixture
{
    public static string RealActionMaps => Path.Combine(AppContext.BaseDirectory, "Fixtures", "actionmaps-real.xml");

    public static string ActionMapsXml(params string[] actionMaps)
    {
        return $"""
        <ActionMaps>
         <ActionProfiles version="1" optionsVersion="2" rebindVersion="2" profileName="default">
          <modifiers />
          {string.Join(Environment.NewLine, actionMaps)}
         </ActionProfiles>
        </ActionMaps>
        """;
    }

    public static string Map(string name, params (string Action, string Input)[] rebinds)
    {
        var actions = rebinds.Select(r => $"<action name=\"{r.Action}\"><rebind input=\"{r.Input}\"/></action>");
        return $"<actionmap name=\"{name}\">{string.Join("", actions)}</actionmap>";
    }
}

public class ScInputTests
{
    [Fact]
    public void Keyboard_WithModifier()
    {
        var input = ScInput.Parse("kb1_lctrl+n");

        Assert.Equal(ScDevice.Keyboard, input.Device);
        Assert.Equal(1, input.Instance);
        Assert.Equal(["LCtrl"], input.Modifiers);
        Assert.Equal("N", input.Key);
        Assert.True(input.TryToKeyPress(60, out var action));
        Assert.Equal("N", action.Key);
        Assert.Equal(["LCtrl"], action.Modifiers);
        Assert.Equal(60, action.PressDurationMs);
    }

    [Fact]
    public void Keyboard_MouseButton_IsMouse()
    {
        var input = ScInput.Parse("kb1_mouse5");

        Assert.Equal(ScDevice.Mouse, input.Device);
        Assert.False(input.TryToKeyPress(60, out _));
    }

    [Fact]
    public void Joystick_Slider()
    {
        var input = ScInput.Parse("js1_slider1");

        Assert.Equal(ScDevice.Joystick, input.Device);
        Assert.Equal(1, input.Instance);
        Assert.False(input.TryToKeyPress(60, out _));
    }

    [Theory]
    [InlineData("kb1_")]
    [InlineData("kb1_ ")]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_IsUnbound(string raw)
    {
        var input = ScInput.Parse(raw);

        Assert.True(input.IsUnbound);
        Assert.False(input.TryToKeyPress(60, out _));
    }

    [Fact]
    public void UnknownKeyName_IsNotSendable()
    {
        var input = ScInput.Parse("kb1_hyperkey");

        Assert.Equal(ScDevice.Keyboard, input.Device);
        Assert.False(input.IsUnbound);
        Assert.False(input.TryToKeyPress(60, out _));
    }

    [Fact]
    public void Comma_IsSendable()
    {
        Assert.True(ScInput.Parse("kb1_comma").TryToKeyPress(60, out var action));
        Assert.Equal("COMMA", action.Key);
    }

    [Fact]
    public void RightAltCombination()
    {
        Assert.True(ScInput.Parse("kb1_ralt+r").TryToKeyPress(60, out var action));
        Assert.Equal(["RAlt"], action.Modifiers);
        Assert.Equal("R", action.Key);
    }

    [Theory]
    [InlineData("kb1_escape", "ESC")]
    [InlineData("kb1_np_5", "NP_5")]
    [InlineData("kb1_f7", "F7")]
    [InlineData("kb1_backspace", "BACKSPACE")]
    [InlineData("kb1_pgup", "PGUP")]
    public void GameKeyNames_AreTranslated(string raw, string key)
    {
        Assert.True(ScInput.Parse(raw).TryToKeyPress(60, out var action));
        Assert.Equal(key, action.Key);
    }

    [Fact]
    public void FourModifiers_IsNotSendable()
    {
        Assert.False(ScInput.Parse("kb1_lctrl+lalt+lshift+rctrl+n").TryToKeyPress(60, out _));
    }

    [Fact]
    public void OrdinaryKeyUsedAsModifier_IsNotSendable()
    {
        Assert.False(ScInput.Parse("kb1_a+b").TryToKeyPress(60, out _));
    }

    [Fact]
    public void UnknownPrefix_IsUnknownDevice()
    {
        Assert.Equal(ScDevice.Unknown, ScInput.Parse("xx9_whatever").Device);
    }
}

public class ActionMapsReaderTests
{
    private static string TempFile(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"versedeck-actionmaps-{Guid.NewGuid():N}.xml");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void RealFile_HasFiveRebinds()
    {
        var file = ActionMapsReader.Read(Fixture.RealActionMaps);

        Assert.True(file.Ok, file.Error);
        Assert.Equal(
            [
                "spaceship_movement/v_autoland=kb1_lctrl+n",
                "spaceship_movement/v_lock_rotation=kb1_mouse5",
                "spaceship_movement/v_strafe_forward=js1_slider1",
                "spaceship_movement/v_toggle_relative_mouse_mode=kb1_comma",
                "player_input_optical_tracking/foip_pushtotalk_proximity=kb1_backslash"
            ],
            file.Rebinds.Select(r => $"{r.ActionMap}/{r.Action}={r.Input.Raw}"));
    }

    [Fact]
    public void MissingFile_ReturnsError_NotException()
    {
        var file = ActionMapsReader.Read(Path.Combine(Path.GetTempPath(), "does-not-exist", "actionmaps.xml"));

        Assert.False(file.Ok);
        Assert.False(string.IsNullOrWhiteSpace(file.Error));
        Assert.Empty(file.Rebinds);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<ActionMaps><ActionProfiles")]
    [InlineData("<Other/>")]
    [InlineData("<ActionMaps/>")]
    public void BrokenFile_ReturnsError(string xml)
    {
        var path = TempFile(xml);
        try
        {
            var file = ActionMapsReader.Read(path);

            Assert.False(file.Ok);
            Assert.False(string.IsNullOrWhiteSpace(file.Error));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ProfileWithoutRebinds_IsOk_AndEmpty()
    {
        var path = TempFile(Fixture.ActionMapsXml());
        try
        {
            var file = ActionMapsReader.Read(path);

            Assert.True(file.Ok);
            Assert.Empty(file.Rebinds);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MultiTap_IsRead()
    {
        var xml = Fixture.ActionMapsXml("<actionmap name=\"m\"><action name=\"a\"><rebind input=\"kb1_x\" multiTap=\"2\"/></action></actionmap>");
        var path = TempFile(xml);
        try
        {
            Assert.Equal(2, ActionMapsReader.Read(path).Rebinds.Single().MultiTap);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ActionWithSeveralRebinds_YieldsEach()
    {
        var xml = Fixture.ActionMapsXml("<actionmap name=\"m\"><action name=\"a\"><rebind input=\"kb1_x\"/><rebind input=\"js1_button3\"/></action></actionmap>");
        var path = TempFile(xml);
        try
        {
            Assert.Equal(["kb1_x", "js1_button3"], ActionMapsReader.Read(path).Rebinds.Select(r => r.Input.Raw));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FileOpenForWritingByAnotherProcess_IsStillRead()
    {
        var path = TempFile(Fixture.ActionMapsXml(Fixture.Map("m", ("a", "kb1_x"))));
        try
        {
            using var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

            Assert.True(ActionMapsReader.Read(path).Ok);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
