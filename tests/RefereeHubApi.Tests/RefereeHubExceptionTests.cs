using Microsoft.AspNetCore.SignalR;
using RefereeHubApi.Hub;

namespace RefereeHubApi.Tests
{
    public class RefereeHubExceptionTests
    {
        [Fact]
        public void ParsesCodedError()
        {
            var ex = RefereeHubException.FromHubException("InvitePlayer",
                new HubException("An unexpected error occurred invoking 'InvitePlayer' on the server. RefereeHubException: Error 19: The specified user is not connected to multiplayer."));

            Assert.Equal(RefereeHubErrorCode.UserNotConnected, ex.Code);
            Assert.Equal(19, ex.RawCode);
            Assert.Equal("The specified user is not connected to multiplayer.", ex.HubMessage);
            Assert.False(ex.IsOpaque);
        }

        [Fact]
        public void ParsesMultilineDetails()
        {
            var ex = RefereeHubException.FromHubException("EditCurrentPlaylistItem",
                new HubException("An unexpected error occurred invoking 'EditCurrentPlaylistItem' on the server. RefereeHubException: Error 12: Invalid mods.\nDetails: Incompatible mods."));

            Assert.Equal(RefereeHubErrorCode.InvalidMods, ex.Code);
            Assert.Equal("Invalid mods.\nDetails: Incompatible mods.", ex.HubMessage);
        }

        [Fact]
        public void OpaqueWhenNoCode()
        {
            var ex = RefereeHubException.FromHubException("MakeRoom", new HubException("An unexpected error occurred invoking 'MakeRoom' on the server."));

            Assert.True(ex.IsOpaque);
            Assert.Equal(RefereeHubErrorCode.Unknown, ex.Code);
        }

        [Fact]
        public void UnknownFutureCodeKeepsRawCode()
        {
            var ex = RefereeHubException.FromHubException("X", new HubException("... HubException: Error 42: Something new."));

            Assert.Equal(RefereeHubErrorCode.Unknown, ex.Code);
            Assert.Equal(42, ex.RawCode);
            Assert.False(ex.IsOpaque);
        }
    }
}
