using System.Reactive.Disposables;
using System.Reactive.Linq;

namespace RefereeHubApi.Timers
{
    public sealed class TimerAnnouncement
    {
        public bool Enabled { get; set; }

        public IReadOnlyList<int> MilestoneSeconds { get; set; } = [60, 30, 10];

        // {0} is the time left ("1:30", "30s").
        public string MilestoneMessage { get; set; } = "Timer: {0} left";

        public string FinishedMessage { get; set; } = "Timer finished";

        public string StartedMessage { get; set; } = "Timer started: {0}";

        public bool AnnounceStart { get; set; } = true;
    }

    // posts timer milestones to a chat (room chat; never PMs). The timer itself stays local.
    public static class TimerAnnouncer
    {
        public static IDisposable Attach(RefereeTimer timer, TimerAnnouncement announcement, Func<string, Task> send, Action<Exception>? onError = null)
        {
            if (!announcement.Enabled)
                return Disposable.Empty;

            async void post(string text)
            {
                try
                {
                    await send(text).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    onError?.Invoke(ex);
                }
            }

            return new CompositeDisposable(
                timer.Milestones.Subscribe(m => post(string.Format(announcement.MilestoneMessage, RefereeTimer.FormatRemaining(m)))),
                timer.Finished.Subscribe(_ => post(announcement.FinishedMessage)));
        }
    }
}
