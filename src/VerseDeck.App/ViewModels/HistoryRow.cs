using System.Globalization;
using VerseDeck.Core.Models;

namespace VerseDeck.App.ViewModels;

/// <summary>One thing the microphone heard and what VerseDeck did with it.</summary>
public sealed record HistoryRow(string Time, string Text, double Confidence, RecognitionOutcome Outcome)
{
    public string ConfidenceText => Confidence.ToString("0.00", CultureInfo.CurrentCulture);

    public string OutcomeText => Outcome switch
    {
        RecognitionOutcome.Executed => "Ejecutado",
        RecognitionOutcome.Discarded => "Descartado: sonaba a conversacion",
        RecognitionOutcome.LowConfidence => "Descartado: confianza baja",
        RecognitionOutcome.GateClosed => "Ignorado: PTT sin pulsar",
        RecognitionOutcome.CopilotSpeaking => "Ignorado: hablaba el copiloto",
        RecognitionOutcome.Repeated => "Ignorado: repeticion",
        RecognitionOutcome.ModuleGone => "Ignorado: el modulo ya no existe",
        RecognitionOutcome.Offline => "Ignorado: voz detenida",
        _ => "No enviado"
    };

    public string OutcomeKey => Outcome switch
    {
        RecognitionOutcome.Executed => "Positive",
        RecognitionOutcome.Failed => "Danger",
        _ => "Muted"
    };
}
