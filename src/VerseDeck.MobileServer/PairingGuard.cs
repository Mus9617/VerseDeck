using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace VerseDeck.MobileServer;

public enum PairStatus { Ok, WrongPin, LockedOut }

public readonly record struct PairResult(PairStatus Status, string? Token);

public sealed class PairingGuard
{
    public const int MaxFailures = 5;
    public static readonly TimeSpan Lockout = TimeSpan.FromMinutes(1);

    private readonly byte[] _pin;
    private readonly Func<DateTimeOffset> _now;
    private readonly ConcurrentDictionary<string, byte> _tokens = new();
    private readonly ConcurrentDictionary<string, (int Failures, DateTimeOffset LockedUntil)> _attempts = new();

    public PairingGuard(string pin, Func<DateTimeOffset> now)
    {
        _pin = Encoding.UTF8.GetBytes(pin);
        _now = now;
    }

    public PairResult TryPair(string remoteAddress, string? pin)
    {
        var now = _now();
        var attempt = _attempts.GetValueOrDefault(remoteAddress);
        if (attempt.LockedUntil > now)
        {
            return new PairResult(PairStatus.LockedOut, null);
        }

        if (attempt.Failures >= MaxFailures)
        {
            attempt = default;
        }

        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(pin ?? string.Empty), _pin))
        {
            var failures = attempt.Failures + 1;
            _attempts[remoteAddress] = (failures, failures >= MaxFailures ? now + Lockout : default);
            return new PairResult(PairStatus.WrongPin, null);
        }

        _attempts.TryRemove(remoteAddress, out _);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        _tokens[token] = 0;
        return new PairResult(PairStatus.Ok, token);
    }

    public bool IsValidToken(string? token)
    {
        return !string.IsNullOrEmpty(token) && _tokens.ContainsKey(token);
    }
}
