using System.Collections.Immutable;
using RefereeHubApi.Hub.Models;

namespace RefereeHubApi.State
{
    public sealed record PlayerState
    {
        public required int UserId { get; init; }
        public MatchUserStatus Status { get; init; } = MatchUserStatus.Idle;
        public MatchTeam? Team { get; init; }

        public int? Slot { get; init; }

        public ImmutableArray<Mod> Mods { get; init; } = ImmutableArray<Mod>.Empty;

        public int? StyleBeatmapId { get; init; }
        public int? StyleRulesetId { get; init; }
    }

    public sealed record CountdownState(long CountdownId, CountdownType Type, DateTimeOffset EndsAt);

    public sealed record MatchInProgress(
        long PlaylistItemId,
        DateTimeOffset StartedAt,
        MatchType Type,
        ImmutableDictionary<int, MatchTeam> Teams,
        ImmutableDictionary<int, byte> Slots);

    public sealed record RollResult(int UserId, uint Max, uint Result, DateTimeOffset At);

    public enum RoomEndReason
    {
        ClosedOrKicked,

        RefereeRemoved,
    }

    public sealed record RoomSnapshot
    {
        public required long RoomId { get; init; }
        public int ChatChannelId { get; init; }
        public string Name { get; init; } = string.Empty;
        public string Password { get; init; } = string.Empty;
        public MatchType MatchType { get; init; }
        public QueueMode QueueMode { get; init; }

        public byte? MaxParticipants { get; init; }

        // right after a join this is a guess (first unplayed item); the next RoomSettingsChanged gives the real one.
        public long? CurrentPlaylistItemId { get; init; }

        public bool Locked { get; init; }

        public ImmutableArray<int?> Slots { get; init; } = ImmutableArray<int?>.Empty;

        public ImmutableDictionary<int, PlayerState> Players { get; init; } = ImmutableDictionary<int, PlayerState>.Empty;

        // everyone known to have referee rights; JoinedReferees is the subset currently in the room.
        public ImmutableHashSet<int> Referees { get; init; } = ImmutableHashSet<int>.Empty;

        public ImmutableHashSet<int> JoinedReferees { get; init; } = ImmutableHashSet<int>.Empty;

        // only bans seen by this client; the hub has no way to list earlier ones.
        public ImmutableHashSet<long> BannedUsers { get; init; } = ImmutableHashSet<long>.Empty;

        public ImmutableList<PlaylistItem> Playlist { get; init; } = ImmutableList<PlaylistItem>.Empty;

        public ImmutableList<CountdownState> Countdowns { get; init; } = ImmutableList<CountdownState>.Empty;

        public MatchInProgress? Match { get; init; }

        public long? LastCompletedPlaylistItemId { get; init; }

        public RollResult? LastRoll { get; init; }

        public RoomEndReason? Ended { get; init; }

        public PlaylistItem? CurrentPlaylistItem
            => CurrentPlaylistItemId is long id ? Playlist.FirstOrDefault(p => p.PlaylistItemId == id) : null;

        public CountdownState? MatchStartCountdown
            => Countdowns.FirstOrDefault(c => c.Type == CountdownType.MatchStart);

        public bool IsMatchInProgress => Match != null;
    }
}
