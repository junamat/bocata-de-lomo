using System.Text.Json;
using System.Text.Json.Serialization;
using RefereeHubApi.IO;

namespace RefereeHubApi.Settings
{
    public class SettingsException : Exception
    {
        public SettingsException(string message, Exception? inner = null)
            : base(message, inner)
        {
        }
    }

    // settings.json in the OS config dir (~/.config/RefereeHubApi on Linux). Writes are atomic (temp file + rename).
    public sealed class SettingsStore
    {
        public const string APP_DIRECTORY = "RefereeHubApi";
        public const string FILE_NAME = "settings.json";

        public static readonly JsonSerializerOptions JSON_OPTIONS = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        public string FilePath { get; }

        public SettingsStore(string? filePath = null)
        {
            FilePath = filePath ?? Path.Combine(DefaultDirectory, FILE_NAME);
        }

        public static string DefaultDirectory
            => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify), APP_DIRECTORY);

        // defaults when there's no file yet. A file that can't be read is an error rather than silently replaced.
        public async Task<RefereeSettings> LoadAsync(CancellationToken ct = default)
        {
            if (!File.Exists(FilePath))
                return new RefereeSettings();

            try
            {
                await using var stream = File.OpenRead(FilePath);
                return await JsonSerializer.DeserializeAsync<RefereeSettings>(stream, JSON_OPTIONS, ct).ConfigureAwait(false) ?? new RefereeSettings();
            }
            catch (JsonException ex)
            {
                throw new SettingsException($"Settings file {FilePath} is not valid: {ex.Message}", ex);
            }
        }

        public Task SaveAsync(RefereeSettings settings, CancellationToken ct = default)
            => AtomicFile.WriteAllTextAsync(FilePath, JsonSerializer.Serialize(settings, JSON_OPTIONS), ct);
    }
}
