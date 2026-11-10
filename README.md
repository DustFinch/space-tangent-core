# SpaceTangent Core

Engine-independent C# libraries for astronomical simulation. `SpaceTangent.Contracts` is the home for engine-independent exchange types. `SpaceTangent.Core` is the home for simulation algorithms. Both target .NET 8. The SDK version is set in `global.json`.

```sh
dotnet restore SpaceTangent.Core.sln --locked-mode
dotnet build SpaceTangent.Core.sln --no-restore
dotnet test SpaceTangent.Core.sln --no-build --no-restore
```

License: GPL-2.0-or-later. See `COPYING`.

## Local Git checks

Install Python 3 and run `git config --local core.hooksPath .githooks` in each clone. Set the intended public Git author name and GitHub noreply email in the clone.

Place the shared UTF-8 `.stopwords` file next to both repositories, or set `SPACE_TANGENT_STOPWORDS` to its absolute path outside the checkout. It accepts one phrase per line, comments starting with `#`, and optional `[en]` and `[ru]` section headers. All hooks require the file.

The checks cover staged paths and supported text, commit identities and messages, and objects introduced by a push, including annotated tags. For a required literal in text, add a reviewed exception to `scripts/audit/allowlist.json` with `path` (repository-relative), `sha256` (SHA-256 of the exact UTF-8 line without its newline) and `reason`. Changing the line invalidates the exception. Paths, commit metadata and the word list itself are outside the exception mechanism.

Text inspection supports UTF-8 and UTF-16 with a BOM. Binary data and dependency repository histories require their own checks. Each dependency repository has its own hooks and publishes its referenced commit before an application push.
