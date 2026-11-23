using System.Globalization;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;

namespace RefereeHubApi.Timers
{
    public sealed record TimerTick(TimeSpan Remaining, TimeSpan Duration, DateTimeOffset EndsAt);

    // local countdown for the ref (pick time, ready-up time, ...). The hub has no generic timer, so this one has no
    // effect on the room; it never calls the hub. Ticks land on whole multiples of the tick interval before the end.
    public sealed class RefereeTimer : IDisposable
    {
        public static readonly TimeSpan DEFAULT_TICK = TimeSpan.FromSeconds(1);

        private readonly TimeProvider time;
        private readonly TimeSpan tick;
        private readonly object sync = new object();
        private readonly Subject<TimerTick> ticks = new Subject<TimerTick>();
        private readonly Subject<TimeSpan> milestones = new Subject<TimeSpan>();
        private readonly Subject<Unit> finished = new Subject<Unit>();
        private readonly Subject<Unit> stopped = new Subject<Unit>();

        private ITimer? timer;
        private int generation;
        private DateTimeOffset endsAt;
        private TimeSpan duration;
        private List<TimeSpan> pendingMilestones = new List<TimeSpan>();
        private bool disposed;

        public RefereeTimer(TimeProvider? time = null, TimeSpan? tickInterval = null)
        {
            this.time = time ?? TimeProvider.System;
            tick = tickInterval ?? DEFAULT_TICK;

            if (tick <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(tickInterval));
        }

        public bool IsRunning
        {
            get
            {
                lock (sync)
                    return timer != null;
            }
        }

        public TimeSpan Remaining
        {
            get
            {
                lock (sync)
                    return timer == null ? TimeSpan.Zero : clampRemaining(endsAt - time.GetUtcNow());
            }
        }

        public DateTimeOffset? EndsAt
        {
            get
            {
                lock (sync)
                    return timer == null ? null : endsAt;
            }
        }

        // once at start, then every tick, ending with Remaining = 0.
        public IObservable<TimerTick> Ticks => ticks.AsObservable();

        // the milestone (time remaining) when it's reached; each fires once per run.
        public IObservable<TimeSpan> Milestones => milestones.AsObservable();

        public IObservable<Unit> Finished => finished.AsObservable();

        // stopped (or restarted) before finishing.
        public IObservable<Unit> Stopped => stopped.AsObservable();

        // restarts if already running. milestones at or beyond the duration are ignored.
        public void Start(TimeSpan duration, IEnumerable<TimeSpan>? milestoneTimes = null)
        {
            if (duration <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(duration), "Duration must be positive.");

            TimerTick first;
            bool wasRunning;

            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);

                wasRunning = cancelLocked();
                this.duration = duration;
                endsAt = time.GetUtcNow() + duration;
                pendingMilestones = (milestoneTimes ?? []).Where(m => m > TimeSpan.Zero && m < duration).Distinct().OrderByDescending(m => m).ToList();
                int gen = ++generation;
                timer = time.CreateTimer(_ => onTimer(gen), null, nextDue(duration), Timeout.InfiniteTimeSpan);
                first = new TimerTick(duration, duration, endsAt);
            }

            if (wasRunning)
                stopped.OnNext(Unit.Default);

            ticks.OnNext(first);
        }

        public void Stop()
        {
            bool wasRunning;

            lock (sync)
                wasRunning = cancelLocked();

            if (wasRunning)
                stopped.OnNext(Unit.Default);
        }

        private bool cancelLocked()
        {
            if (timer == null)
                return false;

            timer.Dispose();
            timer = null;
            generation++;
            return true;
        }

        private void onTimer(int gen)
        {
            TimerTick current;
            var reached = new List<TimeSpan>();
            bool done;

            lock (sync)
            {
                if (gen != generation || timer == null)
                    return;

                var remaining = clampRemaining(endsAt - time.GetUtcNow());
                done = remaining <= TimeSpan.Zero;

                while (pendingMilestones.Count > 0 && pendingMilestones[0] >= remaining)
                {
                    reached.Add(pendingMilestones[0]);
                    pendingMilestones.RemoveAt(0);
                }

                current = new TimerTick(remaining, duration, endsAt);

                if (done)
                {
                    timer.Dispose();
                    timer = null;
                    generation++;
                }
                else
                {
                    timer.Change(nextDue(remaining), Timeout.InfiniteTimeSpan);
                }
            }

            ticks.OnNext(current);

            foreach (var m in reached)
                milestones.OnNext(m);

            if (done)
                finished.OnNext(Unit.Default);
        }

        // time until remaining reaches the next lower multiple of the tick (or zero).
        private TimeSpan nextDue(TimeSpan remaining)
        {
            long steps = (long)Math.Ceiling(remaining.Ticks / (double)tick.Ticks) - 1;
            var due = remaining - TimeSpan.FromTicks(steps * tick.Ticks);
            return due > TimeSpan.Zero ? due : tick;
        }

        private static TimeSpan clampRemaining(TimeSpan remaining) => remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;

        // "1:30", "45s".
        public static string FormatRemaining(TimeSpan remaining)
        {
            int seconds = (int)Math.Ceiling(remaining.TotalSeconds);
            return seconds >= 60
                ? string.Create(CultureInfo.InvariantCulture, $"{seconds / 60}:{seconds % 60:00}")
                : string.Create(CultureInfo.InvariantCulture, $"{seconds}s");
        }

        public void Dispose()
        {
            lock (sync)
            {
                if (disposed)
                    return;

                disposed = true;
                cancelLocked();
            }

            ticks.OnCompleted();
            milestones.OnCompleted();
            finished.OnCompleted();
            stopped.OnCompleted();
        }
    }
}
