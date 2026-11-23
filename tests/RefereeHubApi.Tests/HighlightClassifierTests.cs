using RefereeHubApi.Api;
using RefereeHubApi.Chat;
using RefereeHubApi.Highlights;
using RefereeHubApi.Tests.Infrastructure;

namespace RefereeHubApi.Tests
{
    public class HighlightClassifierTests
    {
        private static readonly HighlightContext room = new HighlightContext(TestIds.SELF, "Test Ref", ChatChannelKind.Room, 1000);
        private static readonly HighlightContext pm = room with { ChannelKind = ChatChannelKind.Private, RoomId = null };

        private static ChatMessage msg(string content, int sender = TestIds.PLAYER_A)
            => new ChatMessage { MessageId = 1, SenderId = sender, Content = content, ChannelId = 555 };

        private static HighlightKind kinds(string content, HighlightContext? context = null, HighlightRules? rules = null)
            => HighlightClassifier.Classify(msg(content), context ?? room, rules ?? new HighlightRules())?.Kinds ?? HighlightKind.None;

        [Theory]
        [InlineData("hey test ref, ready?")]
        [InlineData("TEST_REF can we start")]
        [InlineData("@Test_Ref!")]
        public void MentionIsCaseInsensitiveAndSpaceEqualsUnderscore(string text)
            => Assert.True(kinds(text).HasFlag(HighlightKind.Mention));

        [Fact]
        public void MentionNeedsWordBoundary() => Assert.False(kinds("test refresh").HasFlag(HighlightKind.Mention));

        [Theory]
        [InlineData("ref pls", true)]
        [InlineData("can the REFEREE check", true)]
        [InlineData("ref?", true)]
        [InlineData("refresh the page", false)]
        [InlineData("referral link", false)]
        [InlineData("prefer this", false)]
        public void RefereeWordsInRoomChat(string text, bool expected)
            => Assert.Equal(expected, kinds(text).HasFlag(HighlightKind.RefereeCall));

        [Fact]
        public void RefereeWordsIgnoredInPrivateChat()
            => Assert.Equal(HighlightKind.PrivateMessage, kinds("ref pls", pm));

        [Fact]
        public void KeywordsAreReported()
        {
            var rules = new HighlightRules { Keywords = ["disconnect", "!!", " pause "] };
            var h = HighlightClassifier.Classify(msg("I got a Disconnect, PAUSE!!"), room, rules)!;

            Assert.Equal(HighlightKind.Keyword, h.Kinds);
            Assert.Equal(["disconnect", "!!", "pause"], h.MatchedKeywords);
            Assert.Equal(1000, h.RoomId);
        }

        [Fact]
        public void CombinedKindsAndRulesCanDisable()
        {
            Assert.Equal(HighlightKind.Mention | HighlightKind.PrivateMessage, kinds("test ref hi", pm));
            Assert.Equal(HighlightKind.None, kinds("test ref, ref", rules: new HighlightRules { Mentions = false, RefereeCalls = false }));
            Assert.Equal(HighlightKind.None, kinds("hello", pm, new HighlightRules { PrivateMessages = false }));
        }

        [Fact]
        public void OwnMessagesNeverHighlight()
            => Assert.Null(HighlightClassifier.Classify(msg("test ref ref", sender: TestIds.SELF), pm, new HighlightRules { Keywords = ["ref"] }));

        [Fact]
        public void Deterministic()
        {
            var rules = new HighlightRules { Keywords = ["gg"] };
            var a = HighlightClassifier.Classify(msg("gg test ref"), room, rules)!;
            var b = HighlightClassifier.Classify(msg("gg test ref"), room, rules)!;

            Assert.Equal(a.Kinds, b.Kinds);
            Assert.Equal(a.MatchedKeywords, b.MatchedKeywords);
        }
    }
}
