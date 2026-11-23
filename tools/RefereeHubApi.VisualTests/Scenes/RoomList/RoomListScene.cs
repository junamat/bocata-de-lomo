using RefereeHubApi.Hub.Models.Events;
using RefereeHubApi.Journal;
using RefereeHubApi.VisualTests.Framework;

namespace RefereeHubApi.VisualTests.Scenes.RoomList;

public sealed class RoomListScene : AppScene
{
    private const long invited_room = 1284702;

    public override string Description => "Home page with one open room, saved journal entries and an incoming referee invite.";

    [Test]
    public void TestOpenRoom()
    {
        AddStep("show rooms", () => Main.ShowRooms());
        AddAssert("one open room", () => Main.RoomsPage!.Rooms.Count == 1);
    }

    [Test]
    public void TestJournal()
    {
        AddRepeatStep("save a journal entry", async i =>
        {
            await Services.Journal.SaveAsync(new RoomJournalEntry
            {
                RoomId = 777 + i,
                Name = i switch
                {
                    0 => "TEST: (Team Halcyon) vs (Team Ember)",
                    1 => "TEST: (Team Aurora) vs (Team Ember)",
                    _ => "TEST: (Team Nightfall) vs (Team Halcyon)",
                },
                StartedAt = DateTimeOffset.UtcNow.AddHours(-2 - i),
                SavedAt = DateTimeOffset.UtcNow.AddHours(-1 - i),
            });
            await Main.RoomsPage!.LoadJournalAsync();
        }, 3);
        AddStep("show rooms", () => Main.ShowRooms());
        AddAssert("journal lists three rooms", () => Main.RoomsPage!.Journal.Count == 3);
    }

    [Test]
    public void TestInvite()
    {
        AddStep("show rooms", () => Main.ShowRooms());
        AddStep("hub sends a referee invite", () => SendAsync("RefereeInvited", new RefereeInvitedEvent { RoomId = invited_room }));
        AddUntilStep("invite banner shows", () => Main.RoomsPage!.InvitedRoomId == invited_room);
    }
}
