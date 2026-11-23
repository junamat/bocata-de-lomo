using System.Reactive.Disposables;
using System.Reactive.Linq;
using RefereeHubApi.Session;

namespace RefereeHubApi.Journal
{
    // keeps one room's journal entry up to date (debounced) and deletes it when the room ends.
    internal sealed class RoomJournalRecorder
    {
        private readonly RoomJournal journal;
        private readonly RefereeRoom room;
        private readonly TimeProvider time;
        private readonly TimeSpan delay;
        private readonly Action<Exception> onError;
        private readonly SemaphoreSlim writeLock = new SemaphoreSlim(1, 1);
        private readonly CompositeDisposable subscriptions = new CompositeDisposable();
        private readonly object sync = new object();
        private bool saveScheduled;
        private bool ended;

        private RoomJournalRecorder(RoomJournal journal, RefereeRoom room, TimeProvider time, TimeSpan delay, Action<Exception> onError)
        {
            this.journal = journal;
            this.room = room;
            this.time = time;
            this.delay = delay;
            this.onError = onError;
        }

        // earlier history for this room (e.g. from before a restart) goes into the transcript; its snapshot is ignored.
        public static async Task<RoomJournalRecorder> AttachAsync(RoomJournal journal, RefereeRoom room, TimeProvider time, TimeSpan delay, Action<Exception> onError, CancellationToken ct)
        {
            var recorder = new RoomJournalRecorder(journal, room, time, delay, onError);

            try
            {
                if (await journal.LoadAsync(room.RoomId, ct).ConfigureAwait(false) is { } earlier)
                    room.Transcript.Seed(earlier.Chat, earlier.Results);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                onError(ex);
            }

            recorder.subscriptions.Add(room.StateChanged.Subscribe(_ => recorder.markDirty()));
            recorder.subscriptions.Add(room.Transcript.Changed.Subscribe(_ => recorder.markDirty()));
            return recorder;
        }

        private void markDirty()
        {
            lock (sync)
            {
                if (ended || saveScheduled)
                    return;

                saveScheduled = true;
            }

            _ = saveLater();
        }

        private async Task saveLater()
        {
            try
            {
                await Task.Delay(delay, time).ConfigureAwait(false);
            }
            finally
            {
                lock (sync)
                    saveScheduled = false;
            }

            await SaveAsync().ConfigureAwait(false);
        }

        public async Task SaveAsync()
        {
            await writeLock.WaitAsync().ConfigureAwait(false);

            try
            {
                lock (sync)
                {
                    if (ended)
                        return;
                }

                var state = room.State;
                await journal.SaveAsync(new RoomJournalEntry
                {
                    RoomId = room.RoomId,
                    Name = state.Name,
                    StartedAt = room.Transcript.StartedAt,
                    SavedAt = time.GetUtcNow(),
                    Snapshot = state,
                    Chat = room.Transcript.Messages.ToList(),
                    Results = room.Transcript.Results.ToList(),
                }).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                onError(ex);
            }
            finally
            {
                writeLock.Release();
            }
        }

        // the room is over: no more saves, entry removed.
        public async Task EndAsync()
        {
            lock (sync)
                ended = true;

            subscriptions.Dispose();
            await writeLock.WaitAsync().ConfigureAwait(false);

            try
            {
                await journal.DeleteAsync(room.RoomId).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                onError(ex);
            }
            finally
            {
                writeLock.Release();
            }
        }

        // session going away while the room is still open: write the latest state and stop.
        public async Task DetachAsync()
        {
            subscriptions.Dispose();
            await SaveAsync().ConfigureAwait(false);

            lock (sync)
                ended = true;
        }
    }
}
