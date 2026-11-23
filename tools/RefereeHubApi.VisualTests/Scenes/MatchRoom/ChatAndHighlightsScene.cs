using RefereeHubApi.Chat;
using RefereeHubApi.Highlights;
using RefereeHubApi.Tests.Infrastructure;
using RefereeHubApi.VisualTests.Framework;

namespace RefereeHubApi.VisualTests.Scenes.MatchRoom;

public sealed class ChatAndHighlightsScene : AppScene
{
    public override string Description => "Mid-match room: bans in chat, a mention highlight, the score, command hints and the activity tab.";

    [SetUpSteps]
    public void ShowRoom() => AddStep("show room", () => Main.ShowRoom(Room));

    [Test]
    public void TestChat()
    {
        AddStep("fill chat", () =>
        {
            Room.Room.Transcript.Add(Fixtures.Message(1, 100, "we ban HD3"));
            Room.Room.Transcript.Add(Fixtures.Message(2, 103, "NM2 ban"));
            Room.Room.Transcript.Add(Fixtures.Message(3, TestIds.SELF, "Aurora 3 - 2 Nightfall. Nightfall to pick."));
            Room.Room.Transcript.Add(Fixtures.Message(4, 105, "TestRef is velvetfox our sub?"));
        });
        AddStep("mention highlight", () => Room.OnHighlight(new Highlight(HighlightKind.Mention, Fixtures.Message(4, 105, ""), ChatChannelKind.Room, Fixtures.ROOM_ID, [])));
        AddUntilStep("chat shows messages", () => Room.Chat.Count >= 4);
        AddAssert("last line tagged as mention", () => Room.Chat.Last().Tag == "MENTION");
    }

    [Test]
    public void TestRoomHeader()
    {
        AddAssert("team names read from the room name", () => Room.RedName == "AURORA");
        AddAssert("three red players", () => Room.Teams[0].Players.Length == 3);
        AddAssert("four maps played", () => Room.PlayedCount == 4);
        AddAssert("room locked", () => Room.Locked);
        AddToggleStep("show password", v => Room.PasswordShown = v);
    }

    [Test]
    public void TestScore()
    {
        AddStep("set score 3-2", () =>
        {
            Room.ScoreRed = 3;
            Room.ScoreBlue = 2;
        });
        AddAssert("pips follow the score", () => Room.Pips.Count > 0);
        AddSliderStep("red score", 0, 5, 3, v => Room.ScoreRed = v);
        AddSliderStep("blue score", 0, 5, 2, v => Room.ScoreBlue = v);
        AddSliderStep("best of", 1, 13, 9, v => Room.BestOf = v);
    }

    [Test]
    public void TestCommandHints()
    {
        AddStep("type /ti", () => Room.ChatInput = "/ti");
        AddAssert("command hints open", () => Room.ShowHints);
        AddStep("clear input", () => Room.ChatInput = string.Empty);
        AddAssert("command hints closed", () => !Room.ShowHints);
    }

    [Test]
    public void TestActivityTab()
    {
        AddStep("switch to activity tab", () => Room.SelectActivityCommand.Execute(null));
        AddAssert("chat hidden", () => !Room.ShowChat);
    }
}
