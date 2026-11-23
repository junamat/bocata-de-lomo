using System.Globalization;
using RefereeHubApi.Hub.Models;
using RefereeHubApi.Session;
using RefereeHubApi.Settings;
using RefereeHubApi.Users;

namespace RefereeHubApi.Commands
{
    // unknown command, bad arguments, or a command that can't run in the current state. Nothing was sent.
    public class CommandException : Exception
    {
        public CommandException(string message, Exception? inner = null)
            : base(message, inner)
        {
        }
    }

    public sealed class CommandContext
    {
        public RefereeSession? Session { get; set; }

        // the room commands act on; make/join/use change it.
        public RefereeRoom? Room { get; set; }

        public RefereeSettings Settings { get; set; } = new RefereeSettings();

        // informational output (state, scores, help, ...).
        public Action<string> Output { get; set; } = _ => { };

        // where 'log' writes when no path is given.
        public string LogDirectory { get; set; } = Environment.CurrentDirectory;

        internal CommandTable? Table { get; set; }

        public RefereeSession RequireSession() => Session ?? throw new CommandException("Not connected.");

        public RefereeRoom RequireRoom()
            => Room is { HasEnded: false } room ? room : throw new CommandException("No current room: 'make', 'join' or 'use' one first.");
    }

    public sealed class CommandArgs
    {
        private readonly string usage;

        public string Raw { get; }

        public IReadOnlyList<string> Tokens { get; }

        public CommandArgs(string raw, string usage)
        {
            Raw = raw.Trim();
            this.usage = usage;
            Tokens = Raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        }

        public int Count => Tokens.Count;

        public bool Has(int index) => index < Tokens.Count;

        public bool Contains(string token) => Tokens.Any(t => string.Equals(t, token, StringComparison.OrdinalIgnoreCase));

        public CommandException Invalid(string? detail = null) => new CommandException(detail == null ? $"Usage: {usage}" : $"{detail}. Usage: {usage}");

        public void Expect(int min, int max = int.MaxValue)
        {
            if (Count < min || Count > max)
                throw Invalid();
        }

        public string Token(int index) => Has(index) ? Tokens[index] : throw Invalid();

        // everything from token `index` on, with the original spacing.
        public string Text(int index)
        {
            if (!Has(index))
                throw Invalid();

            string rest = Raw;

            for (int i = 0; i < index; i++)
                rest = rest.TrimStart()[Tokens[i].Length..];

            return rest.Trim();
        }

        public int Int(int index, int min = int.MinValue, int max = int.MaxValue)
        {
            string t = Token(index);
            if (!int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) || value < min || value > max)
                throw Invalid($"'{t}' is not a valid number");

            return value;
        }

        public int? OptionalInt(int index, int min = int.MinValue, int max = int.MaxValue) => Has(index) ? Int(index, min, max) : null;

        public long Long(int index)
        {
            string t = Token(index);
            if (!long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) || value < 0)
                throw Invalid($"'{t}' is not a valid id");

            return value;
        }

        public UserRef User(int index)
        {
            try
            {
                return UserRef.Parse(Token(index));
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException)
            {
                throw Invalid(ex.Message);
            }
        }

        // "90", "90s", "2m", "1:30".
        public TimeSpan Duration(int index)
        {
            string t = Token(index).ToLowerInvariant();
            double seconds;

            if (t.Contains(':'))
            {
                string[] parts = t.Split(':');
                if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int m) || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int s) || s >= 60)
                    throw Invalid($"'{t}' is not a valid duration");
                seconds = m * 60 + s;
            }
            else
            {
                double factor = t.EndsWith('m') ? 60 : 1;
                string number = t.TrimEnd('s', 'm');
                if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds))
                    throw Invalid($"'{t}' is not a valid duration");
                seconds *= factor;
            }

            if (seconds <= 0 || seconds > TimeSpan.FromHours(24).TotalSeconds)
                throw Invalid($"'{t}' is not a valid duration");

            return TimeSpan.FromSeconds(seconds);
        }

        // "HD,HR", "none".
        public Mod[] Mods(int index)
        {
            string t = Token(index);
            if (string.Equals(t, "none", StringComparison.OrdinalIgnoreCase) || t == "-")
                return [];

            var acronyms = t.Split(',', StringSplitOptions.RemoveEmptyEntries);
            if (acronyms.Length == 0 || acronyms.Any(a => a.Length is < 2 or > 4 || !a.All(char.IsAsciiLetterOrDigit)))
                throw Invalid($"'{t}' is not a valid mod list");

            return acronyms.Select(a => new Mod(a.ToUpperInvariant())).ToArray();
        }

        public T Choice<T>(int index, IReadOnlyDictionary<string, T> choices)
        {
            string t = Token(index).ToLowerInvariant();
            return choices.TryGetValue(t, out var value) ? value : throw Invalid($"'{t}' is not one of {string.Join("|", choices.Keys)}");
        }
    }

    public delegate Task CommandAction(CommandContext context, CancellationToken cancellationToken);

    // a parsed command with valid arguments, ready to run.
    public sealed record BoundCommand(CommandDefinition Definition, CommandArgs Args, CommandAction Run)
    {
        // set for commands that send a private message (V20: macros may not PM more than one user).
        public UserRef? PrivateMessageTarget { get; init; }

        public bool IsMacro => Definition.Name == CommandTable.MACRO_COMMAND;
    }

    public sealed class CommandDefinition
    {
        public required string Name { get; init; }

        public IReadOnlyList<string> Aliases { get; init; } = [];

        public required string Category { get; init; }

        // e.g. "invite <user>".
        public required string Usage { get; init; }

        public required string Summary { get; init; }

        // validates the arguments and returns what to run; throws CommandException on bad arguments.
        public required Func<CommandArgs, CommandAction> Bind { get; init; }

        // for commands that send a PM: who to (from the arguments).
        public Func<CommandArgs, UserRef>? PrivateMessageTarget { get; init; }

        public IEnumerable<string> Names => Aliases.Prepend(Name);
    }
}
