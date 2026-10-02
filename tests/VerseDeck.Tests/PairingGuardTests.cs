using VerseDeck.MobileServer;

namespace VerseDeck.Tests;

public class PairingGuardTests
{
    private DateTimeOffset _now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    private PairingGuard CreateGuard() => new("7391", () => _now);

    [Fact]
    public void CorrectPin_ReturnsValidToken()
    {
        var guard = CreateGuard();

        var result = guard.TryPair("192.168.1.20", "7391");

        Assert.Equal(PairStatus.Ok, result.Status);
        Assert.True(guard.IsValidToken(result.Token));
    }

    [Fact]
    public void WrongPin_ReturnsWrongPin_AndNoToken()
    {
        var guard = CreateGuard();

        var result = guard.TryPair("192.168.1.20", "0000");

        Assert.Equal(PairStatus.WrongPin, result.Status);
        Assert.Null(result.Token);
    }

    [Fact]
    public void UnknownOrMissingToken_IsInvalid()
    {
        var guard = CreateGuard();

        Assert.False(guard.IsValidToken(null));
        Assert.False(guard.IsValidToken(""));
        Assert.False(guard.IsValidToken("nope"));
    }

    [Fact]
    public void FiveFailures_LockOut_EvenTheCorrectPin()
    {
        var guard = CreateGuard();
        for (var i = 0; i < PairingGuard.MaxFailures; i++)
        {
            guard.TryPair("192.168.1.20", "0000");
        }

        Assert.Equal(PairStatus.LockedOut, guard.TryPair("192.168.1.20", "7391").Status);
    }

    [Fact]
    public void Lockout_Expires_AfterOneMinute()
    {
        var guard = CreateGuard();
        for (var i = 0; i < PairingGuard.MaxFailures; i++)
        {
            guard.TryPair("192.168.1.20", "0000");
        }

        _now += PairingGuard.Lockout + TimeSpan.FromSeconds(1);

        Assert.Equal(PairStatus.Ok, guard.TryPair("192.168.1.20", "7391").Status);
    }

    [Fact]
    public void Lockout_IsPerAddress()
    {
        var guard = CreateGuard();
        for (var i = 0; i < PairingGuard.MaxFailures; i++)
        {
            guard.TryPair("192.168.1.20", "0000");
        }

        Assert.Equal(PairStatus.Ok, guard.TryPair("192.168.1.21", "7391").Status);
    }

    [Fact]
    public void ParallelWrongPins_NeverGetMoreThanMaxFailuresTries()
    {
        var guard = CreateGuard();
        var evaluated = 0;

        Parallel.For(0, 500, _ =>
        {
            if (guard.TryPair("192.168.1.20", "0000").Status == PairStatus.WrongPin)
            {
                Interlocked.Increment(ref evaluated);
            }
        });

        Assert.Equal(PairingGuard.MaxFailures, evaluated);
    }

    [Fact]
    public void FailuresSpreadOverManyAddresses_LockPairingForEveryone()
    {
        var guard = CreateGuard();
        for (var i = 0; i < PairingGuard.MaxGlobalFailures; i++)
        {
            guard.TryPair($"fe80::{i + 1}", "0000");
        }

        Assert.Equal(PairStatus.LockedOut, guard.TryPair("192.168.1.99", "7391").Status);

        _now += PairingGuard.Lockout + TimeSpan.FromSeconds(1);
        Assert.Equal(PairStatus.Ok, guard.TryPair("192.168.1.99", "7391").Status);
    }
}