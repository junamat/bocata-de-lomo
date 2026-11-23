using System.Collections.Immutable;
using RefereeHubApi.Desktop.ViewModels;
using RefereeHubApi.Session;
using RefereeHubApi.State;
using RefereeHubApi.VisualTests.Framework;

namespace RefereeHubApi.VisualTests.Scenes.Results;

public sealed class ScoresScene : AppScene
{
    public override string Description => "Map results at 3-3, with every score in or only some of them.";

    private ResultsViewModel results = null!;

    [TestCase(6)]
    [TestCase(4)]
    [TestCase(1)]
    public void TestScoresArrived(int arrived)
    {
        AddStep("set score 3-3", () =>
        {
            Room.ScoreRed = 3;
            Room.ScoreBlue = 3;
        });
        AddStep($"show results with {arrived} of 6", () =>
        {
            var scores = new[]
            {
                Fixtures.Score(103, 830112, 0.9864, "S"), Fixtures.Score(100, 812440, 0.9821, "S"), Fixtures.Score(101, 744019, 0.9688, "A"),
                Fixtures.Score(104, 721905, 0.9702, "A"), Fixtures.Score(105, 674661, 0.9678, "A"), Fixtures.Score(102, 628447, 0.9127, "F", passed: false),
            }.Take(arrived).ToArray();
            var match = new MatchInProgress(5, DateTimeOffset.UtcNow, MatchType.TeamVersus,
                Fixtures.Players.ToDictionary(p => p.Id, p => p.Team).ToImmutableDictionary(), ImmutableDictionary<int, byte>.Empty);
            results = new ResultsViewModel(Room, new MatchResult(Fixtures.ROOM_ID, 5, match, scores, 6, arrived == 6));
            Main.ShowResults(results);
        });

        if (arrived == 6)
        {
            AddAssert("Nightfall wins", () => results.WinnerName == "NIGHTFALL");
            AddAssert("not partial", () => !results.Partial);
        }
        else
        {
            AddAssert("marked partial", () => results.Partial && results.PartialText == $"{arrived} of 6 scores");
        }
    }
}
