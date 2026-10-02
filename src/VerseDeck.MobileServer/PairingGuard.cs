using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace VerseDeck.MobileServer;

public enum PairStatus { Ok, WrongPin, LockedOut }

public readonly record struct PairResult(PairStatus Status, string? Token);

public sealed class PairingGuard
{
    public const int MaxFailures = 5;
    public const int MaxGlobalFailures = 10;
    public static readonly TimeSpan Lockout = TimeSpan.FromMinutes(1);

    private const int MaxTrackedAddresses = 1024;

    private readonly object _gate = new();
    private readonly byte[] _pin;
    private readonly Func<DateTimeOffset> _now;
    private readonly ConcurrentDictionary<string, byte> _tokens = new();
    private readonly Dictionary<string, (int Failures, DateTimeOffset LockedUntil)> _attempts = new();
    private int _globalFailures;
    private DateTimeOffset _globalWindowStart;
    private DateTimeOffset _globalLockedUntil;

    public PairingGuard(string pin, Func<DateTimeOffset> now)
    {
        _pin = Encoding.UTF8.GetBytes(pin);
        _now = now;
    }

    public PairResult TryPair(string remoteAddress, string? pin)
    {
        lock (_gate)
        {
            var now = _now();
            if (_globalLockedUntil > now)
            {
                return new PairResult(PairStatus.LockedOut, null);
            }

            _attempts.TryGetValue(remoteAddress, out var attempt);
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
                CountGlobalFailure(now);
                return new PairResult(PairStatus.WrongPin, null);
            }

            _attempts.Remove(remoteAddress);
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            _tokens[token] = 0;
            return new PairResult(PairStatus.Ok, token);
        }
    }

    public bool IsValidToken(string? token)
    {
        return !string.IsNullOrEmpty(token) && _tokens.ContainsKey(token);
    }

    // A device that changes address for every guess must not get five fresh tries each time.
    private void CountGlobalFailure(DateTimeOffset now)
    {
        if (now - _globalWindowStart > Lockout)
        {
            _globalWindowStart = now;
            _globalFailures = 0;
        }

        _globalFailures++;
        if (_globalFailures >= MaxGlobalFailures)
        {
            _globalLockedUntil = now + Lockout;
            _globalFailures = 0;
        }

        if (_attempts.Count > MaxTrackedAddresses)
        {
            foreach (var address in _attempts.Where(a => a.Value.LockedUntil <= now).Select(a => a.Key).ToList())
            {
                _attempts.Remove(address);
            }
        }
    }
}
