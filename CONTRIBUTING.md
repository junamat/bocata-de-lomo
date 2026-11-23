# Contributing

Thanks for helping out. Bug reports, fixes and small features are all welcome; for anything bigger, open an issue first so we can agree on the approach before you spend time on it.

## Getting set up

You need the .NET 10 SDK. On Linux, the keychain code uses libsecret (GNOME Keyring / KWallet).

```
dotnet build
dotnet test
dotnet run --project src/RefereeHubApi.Desktop
```

To try things against the real servers you need your own osu! OAuth app; see "Setting up as a ref" in the README. Never commit client secrets, tokens or anything from your keychain.

## Where things go

- Room control, chat, lookups and anything that talks to osu! belongs in the library (`src/RefereeHubApi`). The library must not depend on the UI; state changes reach the UI only through `IObservable<T>`.
- The desktop app (`src/RefereeHubApi.Desktop`) only shows state and calls the library.
- Text commands live in the shared command table, so the harness and the app get the same ones.
- Tournament rules (picks, bans, points, who wins a match) stay out of the library; the ref decides those.

`docs/design.md` explains how the pieces fit and the hub quirks behind some of the odder decisions.

## Code style

Follow what's already there:

- 4 spaces, braces on their own line, file-scoped or block namespaces as in the surrounding code.
- `PascalCase` for public members, `camelCase` for private fields and private methods, `UPPER_SNAKE` for constants.
- Keep comments short and only where the code doesn't explain itself.
- No new warnings.

## Tests

Add or update tests with your change:

- `tests/RefereeHubApi.Tests` for pure logic.
- `tests/RefereeHubApi.HubTests` for anything going through the hub (uses an in-process fake hub).
- `tests/RefereeHubApi.Desktop.Tests` for screens. They render headless against the fake hub and fail on binding errors. Set `REFEREEHUB_SNAPSHOTS=<dir>` to save PNGs and check the result by eye.

`dotnet test` must pass before a PR is merged.

## Commits

Commits follow [Conventional Commits](https://www.conventionalcommits.org/):

```
<type>(<scope>): <summary>
```

- Types: `feat`, `fix`, `refactor`, `perf`, `test`, `docs`, `build`, `ci`, `chore`.
- Scopes (optional): `lib`, `desktop`, `harness`, `tests`, `docs`.
- Summary in the imperative, lower case, no trailing period, about 70 characters at most.
- Breaking changes get a `!` after the type/scope and a `BREAKING CHANGE:` line in the body.

Examples:

```
feat(desktop): add map pool tab to the match room
fix(lib): keep room state when the hub reconnects
docs: explain the 4 room limit in the README
```

## Pull requests

- One topic per PR; keep unrelated cleanups separate.
- Describe what changed and how you tested it. For UI changes, include a screenshot.
- Link the issue it closes, if any.
