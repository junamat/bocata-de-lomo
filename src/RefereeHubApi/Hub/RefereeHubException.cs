using System.Text.RegularExpressions;
using Microsoft.AspNetCore.SignalR;

namespace RefereeHubApi.Hub
{
    public enum RefereeHubErrorCode
    {
        // the hub didn't send a code; the message is SignalR's generic one.
        Unknown = 0,
        UserRestricted = 1,
        UserNotReferee = 2,
        RoomNotJoinable = 3,
        RoomDoesNotExist = 4,
        UserNotInRoom = 5,
        BeatmapDoesNotExist = 6,
        IncorrectMatchType = 7,
        NoActiveCountdown = 8,
        RoomStateInvalidForOperation = 9,
        InvalidRuleset = 10,
        InvalidBeatmapRulesetCombination = 11,
        InvalidMods = 12,
        NoAllowedModsInFreestyle = 13,
        PlaylistItemDoesNotExist = 14,
        CannotPerformOperationOnSelf = 15,
        UserBanned = 16,
        InvalidRoll = 17,
        UserNotHost = 18,
        UserNotConnected = 19,
        RoomNameTooLong = 20,
        InvalidMaxParticipantCount = 21,
    }

    public class RefereeHubException : Exception
    {
        private static readonly Regex error_pattern = new Regex(@"Error (\d+): (.*)\z", RegexOptions.Singleline | RegexOptions.Compiled);

        public RefereeHubErrorCode Code { get; }

        // kept separately so codes newer than this enum aren't lost.
        public int RawCode { get; }

        public string HubMessage { get; }

        public string MethodName { get; }

        public bool IsOpaque => RawCode == 0;

        public RefereeHubException(string methodName, int rawCode, string hubMessage, Exception? inner = null)
            : base(rawCode == 0 ? $"{methodName} failed: {hubMessage}" : $"{methodName} failed with error {rawCode}: {hubMessage}", inner)
        {
            MethodName = methodName;
            RawCode = rawCode;
            Code = Enum.IsDefined(typeof(RefereeHubErrorCode), rawCode) ? (RefereeHubErrorCode)rawCode : RefereeHubErrorCode.Unknown;
            HubMessage = hubMessage;
        }

        public static RefereeHubException FromHubException(string methodName, HubException ex)
        {
            var match = error_pattern.Match(ex.Message);

            if (match.Success && int.TryParse(match.Groups[1].Value, out int code))
                return new RefereeHubException(methodName, code, match.Groups[2].Value, ex);

            return new RefereeHubException(methodName, 0, ex.Message, ex);
        }
    }
}
