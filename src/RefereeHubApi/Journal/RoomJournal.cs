using System.Globalization;
using System.Text.Json;
using RefereeHubApi.Api;
using RefereeHubApi.IO;
using RefereeHubApi.Logs;
using RefereeHubApi.State;

namespace RefereeHubApi.Journal
{
    // what was seen in a room that hasn't ended: last snapshot, chat, results. History only: after a reconnect the
    // live state comes from the hub, never from here. The room password is not stored.
    public sealed class RoomJournalEntry
    {
        public const int CURRENT_VERSION = 1;

        public int Version { get; set; } = CURRENT_VERSION;

        public long RoomId { get; set; }

        public string Name { get; set; } = string.Empty;

        public DateTimeOffset StartedAt { get; set; }

        public DateTimeOffset SavedAt { get; set; }

        public RoomSnapshot? Snapshot { get; set; }

        public List<ChatMessage> Chat { get; set; } = new List<ChatMessage>();

        public List<TranscriptResult> Results { get; set; } = new List<TranscriptResult>();
    }

    // one JSON file per room under the OS local data dir (~/.local/share/RefereeHubApi/journal on Linux).
    // Writes are atomic (temp file + rename).
    public sealed class RoomJournal
    {
        private static readonly JsonSerializerOptions json_options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = false };

        public string Directory { get; }

        public RoomJournal(string? directory = null)
        {
            Directory = directory ?? DefaultDirectory;
        }

        public static string DefaultDirectory
            => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify), "RefereeHubApi", "journal");

        private string pathFor(long roomId) => Path.Combine(Directory, $"room-{roomId.ToString(CultureInfo.InvariantCulture)}.json");

        public Task SaveAsync(RoomJournalEntry entry, CancellationToken ct = default)
        {
            if (entry.Snapshot is { Password.Length: > 0 })
                entry.Snapshot = entry.Snapshot with { Password = string.Empty };

            return AtomicFile.WriteAllTextAsync(pathFor(entry.RoomId), JsonSerializer.Serialize(entry, json_options), ct);
        }

        public async Task<RoomJournalEntry?> LoadAsync(long roomId, CancellationToken ct = default)
        {
            string path = pathFor(roomId);
            if (!File.Exists(path))
                return null;

            try
            {
                await using var stream = File.OpenRead(path);
                return await JsonSerializer.DeserializeAsync<RoomJournalEntry>(stream, json_options, ct).ConfigureAwait(false);
            }
            catch (JsonException)
            {
                // a broken entry is only lost history.
                return null;
            }
        }

        // rooms that weren't finished, newest first. A UI can offer to rejoin them; nothing rejoins automatically.
        public async Task<IReadOnlyList<RoomJournalEntry>> ListAsync(CancellationToken ct = default)
        {
            if (!System.IO.Directory.Exists(Directory))
                return [];

            var entries = new List<RoomJournalEntry>();

            foreach (string file in System.IO.Directory.EnumerateFiles(Directory, "room-*.json"))
            {
                string idText = Path.GetFileNameWithoutExtension(file)["room-".Length..];
                if (!long.TryParse(idText, NumberStyles.None, CultureInfo.InvariantCulture, out long id))
                    continue;

                if (await LoadAsync(id, ct).ConfigureAwait(false) is { } entry)
                    entries.Add(entry);
            }

            return entries.OrderByDescending(e => e.SavedAt).ToArray();
        }

        public Task DeleteAsync(long roomId, CancellationToken ct = default)
        {
            File.Delete(pathFor(roomId));
            return Task.CompletedTask;
        }
    }
}
