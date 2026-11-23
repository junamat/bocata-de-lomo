using RefereeHubApi.Commands;
using RefereeHubApi.Settings;

namespace RefereeHubApi.Tests
{
    public class CommandTableTests
    {
        private static readonly CommandTable table = CommandTable.Default;

        // one valid line per command: every command in the table must be listed here.
        private static readonly string[] valid_lines =
        [
            "rooms", "journal", "make 75 0 TEST: (A) vs (B)", "join 123", "use 123", "state", "close", "reconnect", "reconnect close",
            "invite #100", "kick Some_Player", "ban 100", "addref @123", "removeref someone", "team 100 red", "move 100 3", "lock", "unlock",
            "name TEST: (A) vs (B)", "password hunter2", "password -", "type tv", "queue rr", "size 0", "size 16",
            "map 129891", "map 129891 1", "mods HD,HR", "mods none HD,HR", "mods HD freestyle", "additem 75 0 DT", "edititem 5 75", "removeitem 5", "info", "info 75 HD,DT",
            "start", "start 10", "aborttimer", "abort", "roll", "roll 50", "scores", "scores 5",
            "say hello there", "me waves", "pm Some_Player hi there", "msg 100 hi", "whois peppy",
            "timer 90", "timer 1:30", "timer 2m", "timer stop", "log", "log out/match.txt", "macro warmup", "macros", "help", "help invite",
        ];

        [Fact]
        public void EveryCommandHasAValidExample()
        {
            var covered = valid_lines.Select(l => table.Parse(l).Definition.Name).ToHashSet();
            Assert.All(table.Commands, c => Assert.Contains(c.Name, covered));
        }

        [Theory]
        [InlineData("invite")]
        [InlineData("invite a b")]
        [InlineData("make 75")]
        [InlineData("make x 0 name")]
        [InlineData("map 0")]
        [InlineData("type ffa")]
        [InlineData("size 1")]
        [InlineData("size 17")]
        [InlineData("team 100 green")]
        [InlineData("move 100 16")]
        [InlineData("mods HD,HR HD freestyle")]
        [InlineData("mods H@")]
        [InlineData("roll 0")]
        [InlineData("roll 101")]
        [InlineData("say")]
        [InlineData("pm someone")]
        [InlineData("timer")]
        [InlineData("timer 0")]
        [InlineData("timer 1:75")]
        [InlineData("timer soon")]
        [InlineData("lock now")]
        [InlineData("invite #abc")]
        public void InvalidArgumentsAreRejectedAtParse(string line)
            => Assert.Throws<CommandException>(() => table.Parse(line));

        [Theory]
        [InlineData("hello everyone")]
        [InlineData("/hello")]
        [InlineData("")]
        [InlineData("   ")]
        public void UnknownOrEmptyIsAnErrorNotChat(string line)
            => Assert.Throws<CommandException>(() => table.Parse(line));

        [Fact]
        public void SlashPrefixAndCaseAreAccepted()
        {
            Assert.Equal("invite", table.Parse("/INVITE 100").Definition.Name);
            Assert.Equal("pm", table.Parse("msg 100 hi").Definition.Name);
        }

        [Fact]
        public void PrivateMessageTargetIsKnownAtParse()
        {
            var pm = table.Parse("pm #100 hello");
            Assert.Equal(100, pm.PrivateMessageTarget!.Value.Id);
            Assert.Null(table.Parse("say hello").PrivateMessageTarget);
        }

        [Fact]
        public void TextKeepsSpacing()
        {
            var args = new CommandArgs("Some_Player  hello   there ", "pm <user> <text>");
            Assert.Equal("hello   there", args.Text(1));
        }

        [Fact]
        public void HelpListsEveryCommand()
        {
            string help = table.Help();
            Assert.All(table.Commands, c => Assert.Contains(c.Usage, help));
            Assert.Contains("also: msg", table.Help("pm"));
        }

        [Fact]
        public void ExtendingRejectsDuplicates()
        {
            var extra = new CommandDefinition { Name = "say", Category = "x", Usage = "say", Summary = "x", Bind = _ => (_, _) => Task.CompletedTask };
            Assert.Throws<ArgumentException>(() => table.With([extra]));
        }

        [Fact]
        public async Task CommandsNeedingARoomFailWithoutOne()
        {
            var ctx = new CommandContext();
            await Assert.ThrowsAsync<CommandException>(() => table.ExecuteAsync("say hi", ctx));
            await Assert.ThrowsAsync<CommandException>(() => table.ExecuteAsync("rooms", ctx));
        }

        [Fact]
        public async Task MacroWithABadStepRunsNothing()
        {
            var ran = new List<string>();
            var custom = table.With([new CommandDefinition { Name = "mark", Category = "t", Usage = "mark <x>", Summary = "t", Bind = a => { string x = a.Token(0); return (_, _) => { ran.Add(x); return Task.CompletedTask; }; } }]);
            var ctx = new CommandContext
            {
                Settings = new RefereeSettings
                {
                    Macros =
                    {
                        ["good"] = ["mark 1", "mark 2"],
                        ["bad"] = ["mark 1", "invite", "mark 2"],
                        ["unknown"] = ["mark 1", "frobnicate"],
                        ["nested"] = ["mark 1", "macro good"],
                    },
                },
            };

            await custom.ExecuteAsync("macro good", ctx);
            Assert.Equal(["1", "2"], ran);
            ran.Clear();

            var bad = await Assert.ThrowsAsync<CommandException>(() => custom.ExecuteAsync("macro bad", ctx));
            Assert.Contains("step 2", bad.Message);
            await Assert.ThrowsAsync<CommandException>(() => custom.ExecuteAsync("macro unknown", ctx));
            await Assert.ThrowsAsync<CommandException>(() => custom.ExecuteAsync("macro nested", ctx));
            await Assert.ThrowsAsync<CommandException>(() => custom.ExecuteAsync("macro missing", ctx));
            Assert.Empty(ran);
        }

        [Fact]
        public void DurationsParse()
        {
            Assert.Equal(TimeSpan.FromSeconds(90), new CommandArgs("1:30", "t").Duration(0));
            Assert.Equal(TimeSpan.FromSeconds(90), new CommandArgs("90s", "t").Duration(0));
            Assert.Equal(TimeSpan.FromSeconds(120), new CommandArgs("2m", "t").Duration(0));
            Assert.Equal(TimeSpan.FromSeconds(45), new CommandArgs("45", "t").Duration(0));
        }
    }
}
