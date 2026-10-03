namespace VerseDeck.Speech;

/// <summary>
/// Makes a voice sound like it comes over a ship's comms: telephone band, a little drive and hiss, and the
/// squelch of the channel opening and closing. Applied once when the phrase is cached, never while playing.
/// </summary>
public static class RadioEffect
{
    private const double LowCut = 450;
    private const double HighCut = 2800;
    private const double Drive = 2.2;
    private const float Hiss = 0.006f;
    private const float Squelch = 0.08f;
    private static readonly TimeSpan SquelchLength = TimeSpan.FromMilliseconds(50);

    public static SpeechAudio Apply(SpeechAudio audio)
    {
        var rate = audio.SampleRate;
        var squelch = (int)(rate * SquelchLength.TotalSeconds);
        var output = new float[audio.Samples.Length + 2 * squelch];

        // The same seed every time: the same phrase renders to the same file.
        var random = new Random(7);
        var highPass = Biquad.HighPass(rate, LowCut);
        var lowPass = Biquad.LowPass(rate, HighCut);
        var norm = Math.Tanh(Drive);

        for (var i = 0; i < squelch; i++)
        {
            var fade = 1 - i / (double)squelch;
            output[i] = (float)((random.NextDouble() * 2 - 1) * Squelch * fade);
            output[^(i + 1)] = (float)((random.NextDouble() * 2 - 1) * Squelch * fade);
        }

        for (var i = 0; i < audio.Samples.Length; i++)
        {
            var band = lowPass.Process(highPass.Process(audio.Samples[i]));
            var driven = Math.Tanh(band * Drive) / norm * 0.8;
            output[squelch + i] = (float)driven + (float)(random.NextDouble() * 2 - 1) * Hiss;
        }

        return new SpeechAudio(output, rate);
    }

    // Second-order filter from the Audio EQ Cookbook (R. Bristow-Johnson), Q of a Butterworth.
    private sealed class Biquad
    {
        private readonly double _b0, _b1, _b2, _a1, _a2;
        private double _x1, _x2, _y1, _y2;

        private Biquad(double b0, double b1, double b2, double a0, double a1, double a2)
        {
            _b0 = b0 / a0;
            _b1 = b1 / a0;
            _b2 = b2 / a0;
            _a1 = a1 / a0;
            _a2 = a2 / a0;
        }

        public static Biquad HighPass(int rate, double cutoff)
        {
            var (cos, alpha) = Terms(rate, cutoff);
            return new Biquad((1 + cos) / 2, -(1 + cos), (1 + cos) / 2, 1 + alpha, -2 * cos, 1 - alpha);
        }

        public static Biquad LowPass(int rate, double cutoff)
        {
            var (cos, alpha) = Terms(rate, cutoff);
            return new Biquad((1 - cos) / 2, 1 - cos, (1 - cos) / 2, 1 + alpha, -2 * cos, 1 - alpha);
        }

        public double Process(double x)
        {
            var y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
            _x2 = _x1;
            _x1 = x;
            _y2 = _y1;
            _y1 = y;
            return y;
        }

        private static (double Cos, double Alpha) Terms(int rate, double cutoff)
        {
            var w0 = 2 * Math.PI * Math.Min(cutoff, rate * 0.45) / rate;
            return (Math.Cos(w0), Math.Sin(w0) / (2 * Math.Sqrt(0.5)));
        }
    }
}
