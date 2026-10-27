using PcChecker.Core.Users;
using Xunit;

namespace PcChecker.Core.Tests;

public class UserSessionTests
{
    private static UserSession Session(SessionState state, string user = "Alex", bool current = false) =>
        new(1, user, "DESKTOP", state, null, DateTimeOffset.Now, current);

    [Theory]
    [InlineData(0, SessionState.Active)]
    [InlineData(1, SessionState.Connected)]
    [InlineData(4, SessionState.Disconnected)]
    [InlineData(6, SessionState.Listen)]
    [InlineData(99, SessionState.Unknown)]
    public void Wts_state_maps_to_friendly_value(int wts, SessionState expected) =>
        Assert.Equal(expected, SessionStateMapper.FromWts(wts));

    [Fact]
    public void Every_known_state_has_a_label()
    {
        foreach (SessionState state in Enum.GetValues<SessionState>())
        {
            Assert.False(string.IsNullOrWhiteSpace(SessionStateMapper.Label(state)));
        }
    }

    [Fact]
    public void Active_session_can_be_disconnected_and_signed_off_and_messaged()
    {
        var active = Session(SessionState.Active);
        Assert.True(SessionStateMapper.CanDisconnect(active));
        Assert.True(SessionStateMapper.CanSignOff(active));
        Assert.True(SessionStateMapper.CanSendMessage(active));
    }

    [Fact]
    public void Disconnected_session_cannot_be_disconnected_again_but_can_be_messaged()
    {
        var disconnected = Session(SessionState.Disconnected);
        Assert.False(SessionStateMapper.CanDisconnect(disconnected));
        Assert.True(SessionStateMapper.CanSendMessage(disconnected));
        Assert.True(SessionStateMapper.CanSignOff(disconnected));
    }

    [Fact]
    public void Listening_service_session_is_not_a_signed_in_user()
    {
        var listen = Session(SessionState.Listen, user: string.Empty);
        Assert.False(listen.IsSignedIn);
        Assert.False(SessionStateMapper.CanSignOff(listen));
    }

    [Fact]
    public void Display_name_includes_domain_when_present()
    {
        Assert.Equal("DESKTOP\\Alex", Session(SessionState.Active).DisplayName);
        var noDomain = new UserSession(2, "Sam", null, SessionState.Active, null, null, false);
        Assert.Equal("Sam", noDomain.DisplayName);
    }
}
