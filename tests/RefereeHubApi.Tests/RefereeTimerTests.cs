using Microsoft.Extensions.Time.Testing;
using RefereeHubApi.Timers;

namespace RefereeHubApi.Tests
{
    public class RefereeTimerTests
    {
        private readonly FakeTimeProvider time = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-23T12:00:00Z"));

        [Fact]
        public void TicksEverySecondReachesMilestonesAndFinishes()
        {
            using var timer = new RefereeTimer(time);
            var ticks = new List<TimeSpan>();
            var milestones = new List<TimeSpan>();
            int finished = 0;

            timer.Ticks.Subscribe(t => ticks.Add(t.Remaining));
            timer.Milestones.Subscribe(milestones.Add);
            timer.Finished.Subscribe(_ => finished++);

            timer.Start(TimeSpan.FromSeconds(5), [TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10)]);
            Assert.True(timer.IsRunning);

            for (int i = 0; i < 4; i++)
                time.Advance(TimeSpan.FromSeconds(1));

            Assert.Equal(TimeSpan.FromSeconds(1), timer.Remaining);
            Assert.Equal(0, finished);

            time.Advance(TimeSpan.FromSeconds(1));
            time.Advance(TimeSpan.FromSeconds(5));

            Assert.Equal([5, 4, 3, 2, 1, 0], ticks.Select(t => (int)t.TotalSeconds));
            Assert.Equal([3, 1], milestones.Select(t => (int)t.TotalSeconds));
            Assert.Equal(1, finished);
            Assert.False(timer.IsRunning);
        }

        [Fact]
        public void FractionalDurationAlignsTicksToWholeSecondsBeforeEnd()
        {
            using var timer = new RefereeTimer(time);
            var ticks = new List<double>();
            timer.Ticks.Subscribe(t => ticks.Add(t.Remaining.TotalSeconds));

            timer.Start(TimeSpan.FromSeconds(2.5));
            time.Advance(TimeSpan.FromSeconds(0.5));
            time.Advance(TimeSpan.FromSeconds(1));
            time.Advance(TimeSpan.FromSeconds(1));

            Assert.Equal([2.5, 2, 1, 0], ticks);
        }

        [Fact]
        public void RestartAndStopRaiseStopped()
        {
            using var timer = new RefereeTimer(time);
            int stopped = 0, finished = 0;
            timer.Stopped.Subscribe(_ => stopped++);
            timer.Finished.Subscribe(_ => finished++);

            timer.Start(TimeSpan.FromSeconds(10));
            time.Advance(TimeSpan.FromSeconds(3));
            timer.Start(TimeSpan.FromSeconds(2));
            Assert.Equal(1, stopped);

            time.Advance(TimeSpan.FromSeconds(2));
            Assert.Equal(1, finished);

            timer.Start(TimeSpan.FromSeconds(10));
            timer.Stop();
            timer.Stop();
            time.Advance(TimeSpan.FromSeconds(20));

            Assert.Equal(2, stopped);
            Assert.Equal(1, finished);
            Assert.Equal(TimeSpan.Zero, timer.Remaining);
        }

        [Fact]
        public void AnnouncerPostsMilestonesAndEnd()
        {
            using var timer = new RefereeTimer(time);
            var sent = new List<string>();
            var announcement = new TimerAnnouncement { Enabled = true, MilestoneSeconds = [60, 10] };

            using var _ = TimerAnnouncer.Attach(timer, announcement, text => { sent.Add(text); return Task.CompletedTask; });
            timer.Start(TimeSpan.FromSeconds(90), announcement.MilestoneSeconds.Select(s => TimeSpan.FromSeconds(s)));
            time.Advance(TimeSpan.FromSeconds(91));

            Assert.Equal(["Timer: 1:00 left", "Timer: 10s left", "Timer finished"], sent);
        }

        [Fact]
        public void DisabledAnnouncerPostsNothing()
        {
            using var timer = new RefereeTimer(time);
            var sent = new List<string>();

            using var _ = TimerAnnouncer.Attach(timer, new TimerAnnouncement { Enabled = false }, text => { sent.Add(text); return Task.CompletedTask; });
            timer.Start(TimeSpan.FromSeconds(30), [TimeSpan.FromSeconds(10)]);
            time.Advance(TimeSpan.FromSeconds(31));

            Assert.Empty(sent);
        }

        [Theory]
        [InlineData(90, "1:30")]
        [InlineData(60, "1:00")]
        [InlineData(59.2, "1:00")]
        [InlineData(58.5, "59s")]
        [InlineData(5, "5s")]
        public void FormatsRemaining(double seconds, string expected)
            => Assert.Equal(expected, RefereeTimer.FormatRemaining(TimeSpan.FromSeconds(seconds)));
    }
}
