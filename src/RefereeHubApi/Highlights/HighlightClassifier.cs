using RefereeHubApi.Api;
using RefereeHubApi.Chat;

namespace RefereeHubApi.Highlights
{
    [Flags]
    public enum HighlightKind
    {
        None = 0,

        // own username in the message (case-insensitive, space and underscore interchangeable).
        Mention = 1,

        // "ref" or "referee" as a word, in room chat.
        RefereeCall = 2,

        Keyword = 4,

        PrivateMessage = 8,
    }

    public sealed class HighlightRules
    {
        public bool Mentions { get; set; } = true;

        public bool RefereeCalls { get; set; } = true;

        public bool PrivateMessages { get; set; } = true;

        public IReadOnlyList<string> Keywords { get; set; } = [];
    }

    public sealed record HighlightContext(int SelfUserId, string SelfUsername, ChatChannelKind ChannelKind, long? RoomId = null);

    public sealed record Highlight(HighlightKind Kinds, ChatMessage Message, ChatChannelKind ChannelKind, long? RoomId, IReadOnlyList<string> MatchedKeywords);

    // pure and deterministic: same message, context and rules always give the same answer. No I/O.
    public static class HighlightClassifier
    {
        private static readonly string[] referee_words = ["ref", "referee"];

        public static Highlight? Classify(ChatMessage message, HighlightContext context, HighlightRules rules)
        {
            if (message.SenderId == context.SelfUserId)
                return null;

            var kinds = HighlightKind.None;
            string text = message.Content;

            if (rules.Mentions && !string.IsNullOrWhiteSpace(context.SelfUsername) && ContainsWord(text, context.SelfUsername.Trim()))
                kinds |= HighlightKind.Mention;

            if (rules.RefereeCalls && context.ChannelKind == ChatChannelKind.Room && referee_words.Any(w => ContainsWord(text, w)))
                kinds |= HighlightKind.RefereeCall;

            var matched = rules.Keywords.Where(k => !string.IsNullOrWhiteSpace(k) && ContainsWord(text, k.Trim())).Select(k => k.Trim()).ToArray();
            if (matched.Length > 0)
                kinds |= HighlightKind.Keyword;

            if (rules.PrivateMessages && context.ChannelKind == ChatChannelKind.Private)
                kinds |= HighlightKind.PrivateMessage;

            return kinds == HighlightKind.None ? null : new Highlight(kinds, message, context.ChannelKind, context.RoomId, matched);
        }

        // case-insensitive; a space or underscore in the word matches either in the text. Edges that are word
        // characters must sit on a word boundary ("ref" doesn't match "refresh"); other edges ("!!") need not.
        public static bool ContainsWord(string text, string word)
        {
            if (word.Length == 0 || word.Length > text.Length)
                return false;

            bool boundaryStart = isWordChar(word[0]);
            bool boundaryEnd = isWordChar(word[^1]);

            for (int start = 0; start + word.Length <= text.Length; start++)
            {
                if (boundaryStart && start > 0 && isWordChar(text[start - 1]))
                    continue;

                int end = start + word.Length;

                if (boundaryEnd && end < text.Length && isWordChar(text[end]))
                    continue;

                if (matchesAt(text, start, word))
                    return true;
            }

            return false;
        }

        private static bool matchesAt(string text, int start, string word)
        {
            for (int i = 0; i < word.Length; i++)
            {
                char t = text[start + i];
                char w = word[i];

                if (isSpaceLike(w))
                {
                    if (!isSpaceLike(t))
                        return false;
                }
                else if (char.ToLowerInvariant(t) != char.ToLowerInvariant(w))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool isSpaceLike(char c) => c == ' ' || c == '_';

        private static bool isWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';
    }
}
