using OpenWestwood.Client.Network;
using Xunit;

namespace OpenWestwood.Tests;

/// <summary>A hosted game with a password lets in only machines that know it (the password itself never travels).</summary>
public class NetworkPasswordTests
{
    static (bool Joined, string? Error) TryJoin(int port, string password)
    {
        using var session = NetSession.Join("127.0.0.1", port, "Guest", "test-fingerprint", password);
        // (Generous: the whole test suite may be loading the machine.)
        var until = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < until)
        {
            session.Poll();
            if (session.Error != null)
                return (false, session.Error);
            if (session.ClientId >= 0)
                return (true, null);
            Thread.Sleep(10);
        }
        return (false, "no answer");
    }

    [Fact]
    public void OnlyTheRightPasswordGetsIn()
    {
        using var host = NetSession.HostGame("Test", "Host", 0, lan: false, "test-fingerprint", _ => null, (_, _) => { }, password: "open sesame");
        int port = host.Host!.Port;
        Assert.Equal((false, "Wrong password."), TryJoin(port, "wrong"));
        Assert.Equal((false, "Wrong password."), TryJoin(port, ""));
        Assert.True(TryJoin(port, "open sesame").Joined);
    }

    [Fact]
    public void GuessingIsStoppedAfterFiveWrongPasswords()
    {
        using var host = NetSession.HostGame("Test", "Host", 0, lan: false, "test-fingerprint", _ => null, (_, _) => { }, password: "pw");
        int port = host.Host!.Port;
        for (int i = 0; i < 5; i++)
            Assert.Equal("Wrong password.", TryJoin(port, "guess" + i).Error);
        Assert.Equal("Too many wrong passwords. Try again in a few minutes.", TryJoin(port, "pw").Error);
    }

    [Fact]
    public void AGameWithoutAPasswordLetsEveryoneIn()
    {
        using var host = NetSession.HostGame("Test", "Host", 0, lan: false, "test-fingerprint", _ => null, (_, _) => { });
        Assert.True(TryJoin(host.Host!.Port, "anything").Joined);
    }
}
