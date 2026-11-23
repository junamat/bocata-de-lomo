using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using RefereeHubApi.Api;
using RefereeHubApi.Chat;
using RefereeHubApi.Session;

namespace RefereeHubApi.Logs
{
    public sealed record TranscriptResult(DateTimeOffset At, int? BeatmapId, MatchResult Result);

    // everything seen in one channel, kept in full (the channel's replay buffer is bounded), plus map results for rooms.
    public sealed class ChatTranscript
    {
        private readonly object sync = new object();
        private readonly SortedDictionary<long, ChatMessage> messages = new SortedDictionary<long, ChatMessage>();
        private readonly List<TranscriptResult> results = new List<TranscriptResult>();
        private readonly Subject<Unit> changed = new Subject<Unit>();

        public long ChannelId { get; }

        public string Title { get; set; }

        public DateTimeOffset StartedAt { get; }

        public ChatTranscript(long channelId, string title, DateTimeOffset startedAt)
        {
            ChannelId = channelId;
            Title = title;
            StartedAt = startedAt;
        }

        public IReadOnlyList<ChatMessage> Messages
        {
            get
            {
                lock (sync)
                    return messages.Values.ToArray();
            }
        }

        public IReadOnlyList<TranscriptResult> Results
        {
            get
            {
                lock (sync)
                    return results.ToArray();
            }
        }

        public IObservable<Unit> Changed => changed.AsObservable();

        public bool Add(ChatMessage message)
        {
            lock (sync)
            {
                if (!messages.TryAdd(message.MessageId, message))
                    return false;
            }

            changed.OnNext(Unit.Default);
            return true;
        }

        public void AddResult(TranscriptResult result)
        {
            lock (sync)
            {
                results.RemoveAll(r => r.Result.PlaylistItemId == result.Result.PlaylistItemId);
                results.Add(result);
                results.Sort((a, b) => a.At.CompareTo(b.At));
            }

            changed.OnNext(Unit.Default);
        }

        // earlier history (e.g. from a journal) merged in; duplicates are skipped.
        public void Seed(IEnumerable<ChatMessage> history, IEnumerable<TranscriptResult> earlierResults)
        {
            lock (sync)
            {
                foreach (var m in history)
                    messages.TryAdd(m.MessageId, m);

                foreach (var r in earlierResults)
                {
                    if (results.All(x => x.Result.PlaylistItemId != r.Result.PlaylistItemId))
                        results.Add(r);
                }

                results.Sort((a, b) => a.At.CompareTo(b.At));
            }

            changed.OnNext(Unit.Default);
        }

        public IDisposable Record(ChatChannel channel) => channel.Messages.Subscribe(m => Add(m));

        internal void Complete() => changed.OnCompleted();
    }
}
