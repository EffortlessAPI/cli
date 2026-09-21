# Effortless CLI repository guide

This repository builds the REST-only `effortless` CLI. The official npm package is
`@effortlessapi/cli`, and all four binary names (`effortless`, `ssotme`,
`aicapture`, `aic`) point at `cli.js`. The unscoped npm package `effortless` is
unrelated software and must never be installed as this CLI.

## Source of truth

`effortless-rulebook/effortless-rulebook.json` is the single source of truth for
CLI options, dispatch behavior, wire contracts, messages, files, and tests.
Query it with `jq`; do not read the whole file.

When changing generated options, bareword verbs, the CLI reference, or the test
manifest:

1. Edit the rulebook.
2. Run `npm run generate`.
3. Never hand-edit `*.g.cs` or other generated artifacts.

The only way to add or rename a CLI option is to change its rulebook row and
run `npm run generate`. CI rejects generated-file drift.

`npm run generate` writes six artifacts, all of them off-limits to hand edits:

- `src/Effortless.Cli.Core/Options/CliOptions.g.cs`
- `src/Effortless.Cli.Core/Options/BarewordVerbs.g.cs`
- `src/Effortless.Cli.Core/Options/CliOptionMetadata.g.cs` — tier, category,
  parent, family, help detail and example; what `-help <topic>` reads at runtime
- `docs/cli-reference.md`
- `tests/Effortless.Cli.E2E/TestManifest.g.json`, `tests/.../Tests/TestManifest.g.json`

`README.md` is the exception: only the text between its
`<!-- cli-commands:start -->` and `<!-- cli-commands:end -->` markers is
generated. Edit the rest of the README freely; never edit inside the markers.

Two traps worth knowing before you touch a `CliOptions` row:

- **`HelpSummary` has a 51-character budget**, not 72. `-help` renders
  `"  {Flag,-26} {HelpSummary}"`, so anything longer breaks the 80-column
  guarantee `meta-help-width` asserts. `help-summary-width` enforces it against
  the rulebook so it fails at authoring time rather than in an E2E run.
- **A bareword whose option takes a value needs a generator decision.** The
  `consumedStringOptions` set in `scripts/generate-from-rulebook.mjs` handles
  `verb <value>`; `parserHandledStringOptions` defers to
  `CliArgumentParser.NoDashCommandForms` for `verb <tool> <value>` shapes such as
  `pin`. A value-typed option left out of both is emitted as a `bool` setter and
  will not compile.

## Repository layout

- `src/Effortless.Cli/` — .NET 8 executable.
- `src/Effortless.Cli.Core/` — parser, dispatcher, project, REST, auth, file-set,
  configuration, seeds, cloud-trigger watching, and update logic.
- `tests/Effortless.Cli.Tests/` — unit and wire-contract tests.
- `tests/Effortless.Cli.E2E/` — black-box CLI tests and mock HTTP servers.
- `tests/fixtures/` — project, catalog, wire, shim, and golden fixtures.
- `installers/windows/` and `installers/macos/` — MSI and PKG sources.
- `scripts/` — generation, CI, and release scripts.

## Build and test

```bash
dotnet build Effortless.Cli.sln --configuration Release
dotnet test Effortless.Cli.sln --configuration Release
```

Run `dotnet test` before every commit.

For npm-shim testing:

```bash
npm install -g .
effortless -version
ssotme -v
aicapture -v
aic -v
```

The supported public install is `npm install -g @effortlessapi/cli`.

## Local tool URL debugging

Point a catalog tool at a local HTTP service, run the command or build, then
remove the override:

```bash
effortless -setToolUrl tool-name=http://localhost:PORT
effortless tool-name -debug
effortless -removeToolUrl tool-name
```

Do not add a stale-catalog or alternate-transport fallback. A catalog refresh
failure is a hard failure.

## Retained seed and trigger features

- D1 keeps `listSeeds`, `cloneSeed`, and the `ssotme-seed.json` `$key$`
  replacement hook. A seed is discovered only when its public repository has
  `effortless.json` at the root. Cloning preserves `.git` and does not
  automatically execute downloaded code.
- D2 keeps `build -buildOnTrigger <baseId>` as a lightweight cloud-trigger
  monitor independent of the rulebook editor. Polling failures are fatal; never
  hide them as an unchanged trigger.
- Airtable schema guessing for seed replacements is removed. Do not remove seed
  discovery, cloning, explicit `ssotme-seed.json` `$key$` replacements, or
  `buildOnTrigger`.

## Authentication status

`login`, `projectLogin`, `plan`, and `logout` call the published
`effortless/effortless/effortless-auth` tool, resolved through the normal catalog
(a `tool_urls.json` override for `effortless-auth` wins, which is the local dev
loop: run the tool's `start.sh` on port 30080 and `-setToolUrl effortless-auth=...`).
The service is a preview that always succeeds and enforces nothing; every
command's output says so. A missing catalog entry or a non-2xx is a clear error
and writes no token. `logout` clears local tokens first and calls the service
best-effort without ever triggering a catalog refresh. Normal tool execution and
`buildOnTrigger` never call the auth tool. The tool's source lives in
`api.effortlessapi.com/Versioned-Stable-SSoTme-Tools/tools/effortless/effortless-auth/`.

## FileSet overwrite invariant

Every tool, cloud or local, controls overwriting **per file** through the FileSet
entry it returns: `AlwaysOverwrite` / `OverwriteMode=Always` rewrites the file on
every build; `OverwriteMode=Never` (or no node at all) writes it once, then only
touches it again if its on-disk content still exactly matches what the ledger
last generated (i.e. it was never hand-edited). `FileSetWriter` is the only place
that interprets these, and it must treat every transpiler identically. Never
special-case a tool, a shape, or a path here, and never let a tool shape lose the
ability to declare a mode: a local-tool runtime that cannot express `Never` per
file is a defect.

**This content-match cleanup on `Never` files is intentional, not a bug** —
without it, a `Never` file that the transpiler relocates (renamed, moved to a
different relative path) would leave a stale orphaned copy behind at the old
path forever ("leaking"), since nothing would ever be allowed to remove it. The
clean pass deletes an untouched `Never` file precisely so it can follow a move;
it only refuses to touch a `Never` file whose content has diverged from the
ledger (a real hand-edit).

**Known consequence: this delete+recreate silently drops OS file-mode bits**
(e.g. the executable bit on a generated `.sh` file), on every build, even when
nobody touched the file — because "unchanged content" says nothing about
permissions, and a fresh `File.Create` on a deleted path never inherits the old
mode. Do not try to fix this by skipping the clean/recreate for unmodified
`Never` files — that reintroduces the orphan-leak problem above. The correct
fix lives at the consumer: `-exec` must ensure its target is executable
(`chmod`-equivalent) immediately before running it, rather than assuming a
generated script kept whatever mode it had last time.

## Project save and upgrade invariants

- `EffortlessProject.Save()` merges unknown custom transpiler properties from the
  on-disk `effortless.json`, but must never restore model-owned properties such as
  `PinnedVersion` or `LastVersionUsed`. `PinnedVersion` uses
  `IgnoreAndPopulate`; copying the old value back would silently undo an unpin.
- With no hard pin, resolution uses catalog HEAD. `LastVersionUsed` is
  informational and records what ran; it does not select a version. Upgrade paths
  clear `PinnedVersion`, remove embedded command-line versions, and advance
  `LastVersionUsed` to HEAD.
- **A pin the catalog can still satisfy survives a build (D17).**
  `ProjectToolUpgradePlan.Apply` takes `clearPins`: the automatic currentness
  gate in `CommandDispatcher.EnsureProjectToolsCurrent` passes `false`, so
  `-pin` actually takes effect; only `-upgrade` / `-upgradeAll` pass `true` and
  unpin. A pin that no longer resolves is stale rather than deliberate and the
  gate still clears it, which is what keeps step-03A's "a stale pin must never
  silently break a build" guarantee intact. Do not collapse these two cases back
  into one.

## Packaging: self-contained binaries, no user-facing .NET

Users install `@effortlessapi/cli` with Node only. The CLI ships as a
self-contained single-file binary inside one of six per-platform packages
(`@effortlessapi/cli-<darwin|linux|win32>-<arm64|x64>`), wired up as
`optionalDependencies` with `os`/`cpu` so npm downloads exactly one. The .NET
runtime is bundled *inside* that binary. Nothing is compiled at install time.

Four traps live here:

- **`optionalDependencies` is required, not stylistic.** A hard dependency
  makes npm fail the entire install on any platform whose `os`/`cpu` does not
  match. Optional lets the resolution miss and lets `cli.js` report it.
- **Never reintroduce a `postinstall` hook.** Its only job was triggering the
  first `dotnet build`; there is nothing to build now, and a `postinstall`
  turns any hiccup into a hard *install* failure instead of a clear runtime
  error.
- **Self-contained publish properties are gated on `RuntimeIdentifier`** in
  `Effortless.Cli.csproj`. Setting `SelfContained`/`PublishSingleFile`
  unconditionally also makes a plain `dotnet build` emit into a RID-qualified
  `bin/Release/net8.0/<rid>/`, which breaks developer mode and every
  contributor's build.
- **Do not set `PublishTrimmed`.** `Newtonsoft.Json` is reflection-heavy and
  trimming produces runtime-only failures the test suite will not catch.

All seven `package.json` versions (main + six platform) must match exactly.
`cli.js` enforces it at runtime and `release.sh` enforces it before publishing.
Platform packages are generated by `scripts/build-platform-packages.mjs`, never
hand-written.

## Releases

**"Release it" means run `scripts/release.sh` to completion. Nothing else
counts.** Committing to `main`, saying a fix "is in," tagging a commit by hand,
or talking about rebuilding installers is NOT a release and does not change
`package.json`'s version. `package.json`'s version is a frozen literal from the
v2 rebuild commit until `scripts/release.sh` actually runs — every commit
merged to `main` without running it leaves that version exactly where it was,
with no warning.

This matters beyond bookkeeping, **for developer-mode checkouts only**:
`cli.js`'s developer-mode path (`node cli.js` / `npm install -g .` from a git
checkout) only rebuilds the compiled .NET DLL when `package.json`'s version
string changes, or the DLL doesn't exist yet. If the version never bumps,
`git pull`-ing new commits into an existing checkout install **silently keeps
running the old compiled DLL forever** — no error, no stale-version warning,
`-version` prints the same string either way. The only fix is an actual
version bump, which only `scripts/release.sh` produces.

Published installs are immune to that: they never compile anything. They run a
prebuilt self-contained binary from a per-platform optional dependency, and
`cli.js` refuses to run at all if that package's version does not exactly match
the launcher's (see the skew guard in `cli.js`). This is why `release.sh`
publishes **all six platform packages first and the main package last** — the
main package must never be resolvable before the binary it depends on is.

Commit source changes first, then use the guarded release flow:

```bash
scripts/release.sh
```

The release script owns version stamping, pushing, and release creation. Do not
hand-edit release versions or invent a publish procedure.

**If npm auth is unavailable** (e.g. account recovery in progress), do not
skip the release — run `scripts/release.sh --skip-npm` instead. It still
bumps the version, runs the test suite, commits, tags, pushes, and creates the
GitHub release; it only skips the npm registry publish, and prints the
`npm publish --access public` command to run later to finish the release once
auth is back. A release done this way is installed with:

```bash
git clone https://github.com/EffortlessAPI/cli.git && cd cli && npm install -g .
```

**Verify a release actually happened** — don't trust a claim that it did:
```bash
git log -1 --format="%H %ci %s"          # HEAD should be the "Release vYYYY.M.D.HHMM" commit
node -p "require('./package.json').version"   # should match that tag, not a stale prior value
```
If the latest commit on `main` is not a `Release v...` commit, no release has
happened yet, no matter what was said in conversation.
