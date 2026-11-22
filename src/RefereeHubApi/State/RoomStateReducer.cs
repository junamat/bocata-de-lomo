using System.Collections.Immutable;
using RefereeHubApi.Hub.Models;
using RefereeHubApi.Hub.Models.Events;
using RefereeHubApi.Hub.Models.Responses;

namespace RefereeHubApi.State
{
    // room state only ever changes here, from a join response or a hub event. nothing is updated optimistically.
    public static class RoomStateReducer
    {
        public static RoomSnapshot FromJoin(RoomJoinedResponse response)
        {
            var playlist = response.Playlist.OrderBy(p => p.Order).ToImmutableList();
            var slots = response.State.Slots?.ToImmutableArray() ?? ImmutableArray<int?>.Empty;

            var players = response.Players.ToImmutableDictionary(p => p.UserId, p => new PlayerState
            {
                UserId = p.UserId,
                Status = p.Status,
                Team = p.Team,
                Mods = p.UserMods.ToImmutableArray(),
                StyleBeatmapId = p.Style.BeatmapId,
                StyleRulesetId = p.Style.RulesetId,
                Slot = slotOf(slots, p.UserId),
            });

            var referees = response.Referees.Select(r => r.UserId).ToImmutableHashSet();

            return new RoomSnapshot
            {
                RoomId = response.RoomId,
                ChatChannelId = response.ChatChannelId,
                Name = response.Name,
                Password = response.Password,
                MatchType = response.State.Type,
                QueueMode = response.QueueMode,
                MaxParticipants = response.MaxParticipants == 0 ? null : response.MaxParticipants,
                CurrentPlaylistItemId = (playlist.FirstOrDefault(p => !p.WasPlayed) ?? playlist.LastOrDefault())?.PlaylistItemId,
                Locked = response.State.Locked,
                Slots = slots,
                Players = players,
                Referees = referees,
                JoinedReferees = referees,
                Playlist = playlist,
            };
        }

        public static RoomSnapshot Apply(RoomSnapshot room, IRoomEvent e, int selfUserId, DateTimeOffset now)
        {
            if (e.RoomId != room.RoomId)
                throw new ArgumentException($"Event for room {e.RoomId} applied to room {room.RoomId}.", nameof(e));

            switch (e)
            {
                case UserJoinedEvent ev:
                {
                    int userId = (int)ev.UserId;

                    if (room.Referees.Contains(userId))
                        return room with { JoinedReferees = room.JoinedReferees.Add(userId) };

                    if (room.Players.ContainsKey(userId))
                        return room;

                    return room with { Players = room.Players.Add(userId, new PlayerState { UserId = userId }) };
                }

                case UserLeftEvent ev:
                    return removeUser(room, (int)ev.UserId);

                case UserKickedEvent ev:
                    if (ev.KickedUserId == selfUserId)
                        return removeUser(room, selfUserId) with { Ended = room.Ended ?? RoomEndReason.ClosedOrKicked };

                    return removeUser(room, (int)ev.KickedUserId);

                case UserBannedEvent ev:
                    return removeUser(room, (int)ev.BannedUserId) with { BannedUsers = room.BannedUsers.Add(ev.BannedUserId) };

                case RefereeAddedEvent ev:
                    return room with { Referees = room.Referees.Add(ev.UserId) };

                case RefereeRemovedEvent ev:
                {
                    var updated = room with
                    {
                        Referees = room.Referees.Remove(ev.UserId),
                        JoinedReferees = room.JoinedReferees.Remove(ev.UserId),
                    };

                    return ev.UserId == selfUserId ? updated with { Ended = RoomEndReason.RefereeRemoved } : updated;
                }

                case RefereeInvitedEvent:
                    // targeted at this user about a room it is not tracking yet; nothing to change here.
                    return room;

                case RoomSettingsChangedEvent ev:
                    return room with
                    {
                        Name = ev.Name,
                        Password = ev.Password,
                        MatchType = ev.Type,
                        QueueMode = ev.QueueMode,
                        CurrentPlaylistItemId = ev.PlaylistItemId,
                        MaxParticipants = ev.MaxParticipants == 0 ? null : ev.MaxParticipants,
                    };

                case MatchStateChangedEvent ev:
                {
                    var slots = ev.State.Slots?.ToImmutableArray() ?? ImmutableArray<int?>.Empty;
                    var players = room.Players.ToImmutableDictionary(kv => kv.Key, kv => kv.Value with { Slot = slotOf(slots, kv.Key) });

                    return room with
                    {
                        MatchType = ev.State.Type,
                        Locked = ev.State.Locked,
                        Slots = slots,
                        Players = players,
                    };
                }

                case PlaylistItemAddedEvent ev:
                    return room with { Playlist = sortedPlaylist(room.Playlist.RemoveAll(p => p.PlaylistItemId == ev.PlaylistItem.PlaylistItemId).Add(ev.PlaylistItem)) };

                case PlaylistItemChangedEvent ev:
                    return room with { Playlist = sortedPlaylist(room.Playlist.RemoveAll(p => p.PlaylistItemId == ev.PlaylistItem.PlaylistItemId).Add(ev.PlaylistItem)) };

                case PlaylistItemRemovedEvent ev:
                    return room with { Playlist = room.Playlist.RemoveAll(p => p.PlaylistItemId == ev.PlaylistItemId) };

                case RollCompletedEvent ev:
                    return room with { LastRoll = new RollResult(ev.UserId, ev.Max, ev.Result, now) };

                case UserStatusChangedEvent ev:
                    return updatePlayer(room, ev.UserId, p => p with { Status = ev.Status });

                case UserModsChangedEvent ev:
                    return updatePlayer(room, ev.UserId, p => p with { Mods = ev.UserMods.ToImmutableArray() });

                case UserStyleChangedEvent ev:
                    return updatePlayer(room, ev.UserId, p => p with { StyleBeatmapId = ev.BeatmapId, StyleRulesetId = ev.RulesetId });

                case UserTeamChangedEvent ev:
                    return updatePlayer(room, ev.UserId, p => p with { Team = ev.Team });

                case CountdownStartedEvent ev:
                    return room with
                    {
                        Countdowns = room.Countdowns
                                         .RemoveAll(c => c.CountdownId == ev.CountdownId)
                                         .Add(new CountdownState(ev.CountdownId, ev.Type, now + TimeSpan.FromSeconds(ev.Seconds))),
                    };

                case CountdownStoppedEvent ev:
                    return room with { Countdowns = room.Countdowns.RemoveAll(c => c.CountdownId == ev.CountdownId) };

                case MatchStartedEvent ev:
                    return room with
                    {
                        Match = new MatchInProgress(
                            ev.PlaylistItemId,
                            now,
                            ev.MatchType,
                            ev.Teams?.ToImmutableDictionary() ?? ImmutableDictionary<int, MatchTeam>.Empty,
                            ev.Slots?.ToImmutableDictionary() ?? ImmutableDictionary<int, byte>.Empty),
                    };

                case MatchAbortedEvent:
                    return room with { Match = null };

                case MatchCompletedEvent ev:
                    return room with { Match = null, LastCompletedPlaylistItemId = ev.PlaylistItemId };

                default:
                    return room;
            }
        }

        private static RoomSnapshot removeUser(RoomSnapshot room, int userId) => room with
        {
            Players = room.Players.Remove(userId),
            JoinedReferees = room.JoinedReferees.Remove(userId),
        };

        private static RoomSnapshot updatePlayer(RoomSnapshot room, int userId, Func<PlayerState, PlayerState> update)
        {
            if (!room.Players.ContainsKey(userId) && room.Referees.Contains(userId))
                return room;

            // events can arrive for a player whose UserJoined preceded our join; create them on demand.
            var player = room.Players.TryGetValue(userId, out var existing) ? existing : new PlayerState { UserId = userId };
            return room with { Players = room.Players.SetItem(userId, update(player)) };
        }

        private static ImmutableList<PlaylistItem> sortedPlaylist(ImmutableList<PlaylistItem> playlist)
            => playlist.Sort((a, b) => a.Order != b.Order ? a.Order.CompareTo(b.Order) : a.PlaylistItemId.CompareTo(b.PlaylistItemId));

        private static int? slotOf(ImmutableArray<int?> slots, int userId)
        {
            for (int i = 0; i < slots.Length; i++)
            {
                if (slots[i] == userId)
                    return i;
            }

            return null;
        }
    }
}
