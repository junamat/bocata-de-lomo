using System.Text;
using RefereeHubApi.Users;

namespace RefereeHubApi.Commands
{
    // the one command table, shared by the harness and any UI: text -> validated action. Help is generated from it.
    // Unknown commands and bad arguments throw CommandException before anything is sent; text is never
    // forwarded to chat as a fallback.
    public sealed class CommandTable
    {
        public const string MACRO_COMMAND = "macro";

        private readonly Dictionary<string, CommandDefinition> byName = new Dictionary<string, CommandDefinition>(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<CommandDefinition> Commands { get; }

        public CommandTable(IEnumerable<CommandDefinition> commands)
        {
            Commands = commands.ToArray();

            foreach (var command in Commands)
            {
                foreach (string name in command.Names)
                {
                    if (!byName.TryAdd(name, command))
                        throw new ArgumentException($"Duplicate command name '{name}'.");
                }
            }
        }

        public static CommandTable Default { get; } = new CommandTable(DefaultCommands.Create());

        // e.g. the harness adding setup/login/connect.
        public CommandTable With(IEnumerable<CommandDefinition> extra) => new CommandTable(Commands.Concat(extra));

        public CommandDefinition? Find(string name) => byName.GetValueOrDefault(name);

        // a leading '/' is accepted (chat-box style) and ignored.
        public BoundCommand Parse(string line)
        {
            string text = line.Trim();
            if (text.StartsWith('/'))
                text = text[1..].TrimStart();

            if (text.Length == 0)
                throw new CommandException("Empty command.");

            int space = text.IndexOf(' ');
            string name = space < 0 ? text : text[..space];
            string rest = space < 0 ? string.Empty : text[(space + 1)..];

            var definition = Find(name) ?? throw new CommandException($"Unknown command '{name}'. Type 'help'.");
            var args = new CommandArgs(rest, definition.Usage);
            var run = definition.Bind(args);

            return new BoundCommand(definition, args, run)
            {
                PrivateMessageTarget = definition.PrivateMessageTarget?.Invoke(args),
            };
        }

        public async Task ExecuteAsync(string line, CommandContext context, CancellationToken ct = default)
        {
            var command = Parse(line);
            context.Table ??= this;
            await command.Run(context, ct).ConfigureAwait(false);
        }

        // every step is parsed and checked before the first one runs: one bad line and nothing is sent.
        public async Task RunMacroAsync(string name, CommandContext context, CancellationToken ct = default)
        {
            if (!context.Settings.Macros.TryGetValue(name, out var lines))
                throw new CommandException($"No macro named '{name}'.");

            var steps = new List<BoundCommand>();

            foreach (var (line, index) in lines.Select((l, i) => (l, i)))
            {
                BoundCommand step;

                try
                {
                    step = Parse(line);
                }
                catch (CommandException ex)
                {
                    throw new CommandException($"Macro '{name}' step {index + 1} ('{line}'): {ex.Message}", ex);
                }

                if (step.IsMacro)
                    throw new CommandException($"Macro '{name}' step {index + 1}: macros can't run other macros.");

                steps.Add(step);
            }

            // V20: a macro may PM at most one user.
            var targets = steps.Where(s => s.PrivateMessageTarget != null).Select(s => s.PrivateMessageTarget!.Value).ToArray();

            if (targets.Length > 0)
            {
                var session = context.RequireSession();
                var ids = new HashSet<int>();

                foreach (var t in targets)
                {
                    try
                    {
                        ids.Add(await session.Users.ResolveIdAsync(t, ct).ConfigureAwait(false));
                    }
                    catch (UserNotFoundException ex)
                    {
                        throw new CommandException($"Macro '{name}': {ex.Message}", ex);
                    }
                }

                if (ids.Count > 1)
                    throw new CommandException($"Macro '{name}' sends private messages to {ids.Count} users; a macro may only message one.");
            }

            context.Table ??= this;

            foreach (var step in steps)
                await step.Run(context, ct).ConfigureAwait(false);
        }

        public string Help(string? command = null)
        {
            if (command != null)
            {
                var c = Find(command) ?? throw new CommandException($"Unknown command '{command}'.");
                string aliases = c.Aliases.Count > 0 ? $" (also: {string.Join(", ", c.Aliases)})" : string.Empty;
                return $"{c.Usage}{aliases}\n  {c.Summary}";
            }

            var sb = new StringBuilder();

            foreach (var group in Commands.GroupBy(c => c.Category))
            {
                sb.Append(group.Key).Append(':').Append('\n');
                int width = group.Max(c => c.Usage.Length);

                foreach (var c in group)
                    sb.Append("  ").Append(c.Usage.PadRight(width)).Append("  ").Append(c.Summary).Append('\n');
            }

            return sb.ToString().TrimEnd('\n');
        }
    }
}
