using System.IO;
using System.Windows.Media;

namespace VerseDeck.App.Services;

public sealed class AudioFeedback : IAudioFeedback
{
    private readonly MediaPlayer _welcome = new();
    private readonly MediaPlayer _command = new();

    public AudioFeedback()
    {
        Open(_welcome, "ready.mp3", 0.72);
        Open(_command, "system.mp3", 0.62);
    }

    public void PlayWelcome() => PlayFromStart(_welcome);

    public void PlayCommand() => PlayFromStart(_command);

    private static void Open(MediaPlayer player, string fileName, double volume)
    {
        var path = Path.Combine(AppContext.BaseDirectory, fileName);
        if (File.Exists(path))
        {
            player.Open(new Uri(path));
            player.Volume = volume;
        }
    }

    private static void PlayFromStart(MediaPlayer player)
    {
        try
        {
            player.Position = TimeSpan.Zero;
            player.Play();
        }
        catch
        {
            // Audio feedback is optional and must never interrupt command execution.
        }
    }
}
