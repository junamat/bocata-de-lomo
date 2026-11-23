using System.Reflection;
using RefereeHubApi.Highlights;
using RefereeHubApi.IO;
using RefereeHubApi.Logs;
using RefereeHubApi.Settings;

namespace RefereeHubApi.Tests
{
    public sealed class SettingsStoreTests : IDisposable
    {
        private readonly DirectoryInfo dir = Directory.CreateTempSubdirectory("refereehub-settings-");

        public void Dispose() => dir.Delete(recursive: true);

        private string path => Path.Combine(dir.FullName, "nested", SettingsStore.FILE_NAME);

        [Fact]
        public async Task MissingFileGivesDefaults()
        {
            var settings = await new SettingsStore(path).LoadAsync();

            Assert.Equal(RefereeSettings.CURRENT_VERSION, settings.Version);
            Assert.Empty(settings.Macros);
            Assert.True(settings.Highlights.Mentions);
        }

        [Fact]
        public async Task RoundTripsAsSnakeCaseJson()
        {
            var store = new SettingsStore(path);
            var settings = new RefereeSettings
            {
                Keybinds = { ["F1"] = "warmup" },
                Macros = { ["warmup"] = ["lock", "timer 120", "say Warmup time"] },
                Alerts = { [AlertTriggers.MENTION] = "ping", [AlertTriggers.TIMER_FINISHED] = "bell" },
                Highlights = new HighlightRules { Keywords = ["pause", "disconnect"], RefereeCalls = false },
                ChatLog = new ChatLogFormat { TimeZone = ChatLogTimeZone.Local, IncludeResults = false },
            };
            settings.Timer.Enabled = true;

            await store.SaveAsync(settings);
            string json = await File.ReadAllTextAsync(path);
            var loaded = await store.LoadAsync();

            Assert.Contains("\"referee_calls\": false", json);
            Assert.Contains("\"time_zone\": \"local\"", json);
            Assert.Equal("warmup", loaded.Keybinds["F1"]);
            Assert.Equal(["lock", "timer 120", "say Warmup time"], loaded.Macros["warmup"]);
            Assert.Equal(["pause", "disconnect"], loaded.Highlights.Keywords);
            Assert.False(loaded.Highlights.RefereeCalls);
            Assert.Equal(ChatLogTimeZone.Local, loaded.ChatLog.TimeZone);
            Assert.True(loaded.Timer.Enabled);
            Assert.Equal(["ping"], AlertTriggers.SoundsFor(loaded, HighlightKind.Mention | HighlightKind.Keyword));
        }

        [Fact]
        public async Task SaveReplacesAtomicallyWithoutLeavingTempFiles()
        {
            var store = new SettingsStore(path);
            await store.SaveAsync(new RefereeSettings { Keybinds = { ["F1"] = "a" } });
            await store.SaveAsync(new RefereeSettings { Keybinds = { ["F2"] = "b" } });

            Assert.Equal([SettingsStore.FILE_NAME], Directory.GetFiles(Path.GetDirectoryName(path)!).Select(Path.GetFileName));
            Assert.Equal("b", (await store.LoadAsync()).Keybinds["F2"]);
        }

        [Fact]
        public async Task FailedWriteKeepsOldFile()
        {
            var store = new SettingsStore(path);
            await store.SaveAsync(new RefereeSettings { Keybinds = { ["F1"] = "a" } });

            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AtomicFile.WriteAllTextAsync(path, "garbage", cts.Token));

            Assert.Equal("a", (await store.LoadAsync()).Keybinds["F1"]);
            Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!));
        }

        [Fact]
        public async Task CorruptFileIsAnError()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, "{ not json");

            await Assert.ThrowsAsync<SettingsException>(() => new SettingsStore(path).LoadAsync());
        }

        [Fact]
        public void SettingsHoldNoSecrets()
        {
            string[] forbidden = ["secret", "token", "password", "credential"];
            var seen = new HashSet<Type>();
            var stack = new Stack<Type>([typeof(RefereeSettings)]);

            while (stack.Count > 0)
            {
                var type = stack.Pop();
                if (!seen.Add(type) || type.Namespace?.StartsWith("RefereeHubApi", StringComparison.Ordinal) != true)
                    continue;

                foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    Assert.DoesNotContain(forbidden, f => p.Name.Contains(f, StringComparison.OrdinalIgnoreCase));
                    stack.Push(p.PropertyType);
                    foreach (var arg in p.PropertyType.GetGenericArguments())
                        stack.Push(arg);
                }
            }

            Assert.Contains(typeof(HighlightRules), seen);
        }
    }
}
