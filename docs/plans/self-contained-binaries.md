# Plan — Remove the .NET SDK dependency via self-contained per-platform binaries

**Status:** proposed, not started
**Goal:** `npm install -g @effortlessapi/cli` works on a machine with only Node installed.
No .NET SDK, no .NET runtime, no first-run `dotnet build`.

---

## 1. What changes, and what deliberately does not

| | today | after |
|---|---|---|
| install command | `npm install -g @effortlessapi/cli` | **unchanged** |
| update | `npm install -g @effortlessapi/cli@latest` | **unchanged** |
| uninstall | `npm uninstall -g @effortlessapi/cli` | **unchanged** |
| aliases | `effortless` / `ssotme` / `aicapture` / `aic` | **unchanged** |
| runtime behaviour | C# over HTTPS | **unchanged** |
| Node | required | required |
| **.NET SDK** | **required** | **gone** |
| **first-run `dotnet build` (~30–60s)** | **happens** | **gone** |
| install size | small pkg + SDK already present | ~60–80 MB, one platform only |

This is **not** an installer. No MSI, no PKG, no privileged daemon, no
receipts database, no `/usr/local/bin` writes. Everything stays inside the npm
global prefix as ordinary files, which is what makes the install/update/remove
lifecycle fully scriptable and agent-manageable. The existing MSI/PKG
installers are unaffected by this plan and can continue to exist as a
convenience path for non-npm users.

### Why this is safe to do

The CLI is a REST client, not an engine. `package.json` already calls it
"REST-only command-line client for EffortlessAPI transpilers".
`src/Effortless.Cli.Core/Transpile/TranspileClient.cs` is `HttpClient` +
polling + retry. There is no template engine, no codegen, no formula parser —
all generation happens in hosted tools reached over HTTPS. The only NuGet
dependencies are `Newtonsoft.Json` and `Plossum.CommandLine.Core`. Nothing in
the codebase needs a *shared framework* install, which is exactly the profile
self-contained publishing handles cleanly.

---

## 2. Answering the stale-platform-package risk honestly

**Verdict: low risk, and lower than the footgun it replaces — but it needs
explicit handling, not hope.**

Today's failure mode is genuinely worse. Per `CLAUDE.md`, `cli.js` only
rebuilds the DLL when `package.json`'s version string changes. A `git pull`
into an existing dev install **silently keeps running the old compiled DLL
forever** — no error, no warning, and `-version` prints the same string either
way. This plan deletes that entire class of bug, because nothing is compiled at
install time.

The new failure mode is a **version skew** between `@effortlessapi/cli` and its
platform package. It is strictly better because it is *detectable*: both carry
a version string, so `cli.js` can compare them and fail loudly.

Four cases to handle explicitly in `cli.js`:

1. **Platform package missing entirely** (unsupported OS/arch, or
   `--no-optional`). Detect via `require.resolve` throwing. Error must name the
   detected `process.platform`/`process.arch`, list supported RIDs, and state
   the likely cause (`--no-optional` / `--omit=optional`).
2. **Version mismatch** between the shim and the platform package. Compare the
   two `package.json` versions on every run — it is one `require`, negligible
   cost. Fail with the exact reinstall command.
3. **Binary present but not executable** (npm has historically dropped the +x
   bit in some extraction paths). Check `fs.accessSync(bin, fs.constants.X_OK)`;
   attempt one `chmod 0o755` before failing.
4. **Partial publish** — main package live, platform package not yet on the
   registry. This is the only case that can strand users, and it is prevented
   in the release pipeline (§5), not at runtime.

The residual risk is a *release-pipeline* concern, not a user-experience one.
It is fully mitigated by publishing platform packages **before** the main
package.

---

## 3. Build changes

### 3.1 `src/Effortless.Cli/Effortless.Cli.csproj`

Add self-contained publish properties:

```xml
<PublishSingleFile>true</PublishSingleFile>
<SelfContained>true</SelfContained>
<IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>
<EnableCompressionInSingleFile>true</EnableCompressionInSingleFile>
<InvariantGlobalization>true</InvariantGlobalization>
```

**Do NOT set `PublishTrimmed` in the first pass.** `Newtonsoft.Json` is
reflection-heavy and trimming it produces runtime-only failures that the test
suite may not catch. Compression alone gets the size down acceptably. Revisit
trimming later as a separate, independently-verified change — and only with a
full E2E run against a trimmed binary.

`InvariantGlobalization` avoids the ICU dependency; verify nothing in the CLI
does culture-sensitive string comparison first (`grep -rn "ToLower\|ToUpper\|
Compare" src --include="*.cs"` and check for culture-sensitive overloads).

### 3.2 Target RIDs

| RID | platform package |
|---|---|
| `osx-arm64` | `@effortlessapi/cli-darwin-arm64` |
| `osx-x64` | `@effortlessapi/cli-darwin-x64` |
| `win-x64` | `@effortlessapi/cli-win32-x64` |
| `win-arm64` | `@effortlessapi/cli-win32-arm64` |
| `linux-x64` | `@effortlessapi/cli-linux-x64` |
| `linux-arm64` | `@effortlessapi/cli-linux-arm64` |

Build command per RID:

```bash
dotnet publish src/Effortless.Cli/Effortless.Cli.csproj \
  -c Release -r <rid> --self-contained true \
  -o artifacts/<rid>
```

---

## 4. Package layout

### 4.1 Platform package (one per RID)

```
@effortlessapi/cli-darwin-arm64/
  package.json
  bin/Effortless.Cli        # or Effortless.Cli.exe on win32
```

```json
{
  "name": "@effortlessapi/cli-darwin-arm64",
  "version": "<same version as main package>",
  "os": ["darwin"],
  "cpu": ["arm64"],
  "files": ["bin/"]
}
```

`os`/`cpu` are what make npm skip non-matching packages — users download
**one** binary, not six.

### 4.2 Main package `package.json`

```json
{
  "optionalDependencies": {
    "@effortlessapi/cli-darwin-arm64": "<version>",
    "@effortlessapi/cli-darwin-x64":   "<version>",
    "@effortlessapi/cli-win32-x64":    "<version>",
    "@effortlessapi/cli-win32-arm64":  "<version>",
    "@effortlessapi/cli-linux-x64":    "<version>",
    "@effortlessapi/cli-linux-arm64":  "<version>"
  }
}
```

**`optionalDependencies` is required, not stylistic** — a hard dependency makes
npm fail the whole install on any platform whose `os`/`cpu` doesn't match.

Also:
- **Remove `"postinstall": "node cli.js -version"`.** Its purpose was to
  trigger the first build. There is nothing to build now, and keeping it turns
  any resolution problem into a hard install failure instead of a clear runtime
  error.
- Drop `src/` and `Directory.Build.props` from `files` — no longer shipped.
- Keep `"dotnet": "8.0.410"` only if something else reads it; otherwise remove.

This is the same mechanism esbuild, swc, and Rollup use for native binaries.
Well-worn ground.

---

## 5. `cli.js` rewrite

Shrinks from ~120 lines to ~40. **Delete entirely:**
`syncVersionFromPackageJson()`, the csproj/`CliVersion.cs` stamping, the
`dotnet build` call, and the `.built-version` stamp logic. Those exist only to
manage compile-on-first-run.

New responsibilities, in order:

1. Map `process.platform` + `process.arch` → platform package name.
2. `require.resolve('<pkg>/package.json')` → derive the binary path.
3. Compare platform-package version against the shim's own version.
4. Verify the binary exists and is executable (chmod 0o755 on failure, once).
5. `spawn(bin, process.argv.slice(2), { stdio: 'inherit' })`, forwarding exit
   code and signal exactly as today.

Error messages must be actionable — name the detected platform, the expected
package, both versions on a mismatch, and the exact remediation command.

### The dev-checkout path needs a decision

`npm install -g .` from a git clone currently works because `cli.js` builds
from source. After this change there is no prebuilt binary in a fresh clone.
**Recommended:** if no platform package resolves **and** `src/` exists next to
`cli.js` (i.e. this is a git checkout, not a published install), fall back to
`dotnet run`/`dotnet build` with a clear "developer mode" notice on stderr.
This keeps the contributor workflow intact — contributors have the SDK anyway —
while published installs never touch it.

Update `CLAUDE.md`'s release section accordingly: the "silently runs the old
DLL forever" warning becomes obsolete for published installs, but still applies
to the dev-mode fallback.

---

## 6. Release pipeline

`scripts/release.sh` currently stamps the version, tests, commits, tags,
pushes, creates the GitHub release, and publishes one npm package. It gains a
publish matrix. **Order is critical.**

1. Stamp version into main `package.json`, all six platform `package.json`s,
   `Effortless.Cli.csproj`, and `CliVersion.cs`. All seven must match exactly.
2. Run the existing test suites (unchanged).
3. `dotnet publish` all six RIDs.
4. Smoke-test each binary that can run on the build host (`-version`).
5. **Publish all six platform packages first.**
6. **Publish the main package last** — only after every platform publish
   succeeded.

Step 6-after-5 is the entire mitigation for the partial-publish risk. A user
can never resolve a main package whose platform packages aren't on the registry
yet, because the main package doesn't exist until they are.

Preserve `--dry-run` and `--skip-npm`. Under `--skip-npm`, print all seven
`npm publish` commands in the correct order.

Cross-compilation note: `dotnet publish -r <rid>` cross-compiles fine from any
host for these RIDs, so a single Linux CI job can build all six. Code signing
is the exception — macOS binaries distributed via npm are not Gatekeeper-
checked the way a downloaded PKG is, so no notarization is required for the npm
path. The existing signed PKG/MSI flow is untouched.

---

## 7. Verification

Before considering this done:

- [ ] Clean machine (or container) with **Node only, no .NET**:
      `npm install -g @effortlessapi/cli` → `effortless -version` works.
- [ ] Confirm only one platform package landed in `node_modules`.
- [ ] `effortless build` completes end-to-end against a real project.
- [ ] `npm install -g @effortlessapi/cli@latest` upgrades cleanly.
- [ ] `npm uninstall -g @effortlessapi/cli` removes cleanly.
- [ ] All four aliases work.
- [ ] Forced version mismatch produces the actionable error, not a crash.
- [ ] `npm install --no-optional` produces the actionable error, not a crash.
- [ ] Dev checkout (`npm install -g .`) still works via the SDK fallback.
- [ ] Existing .NET test suites still pass.
- [ ] `scripts/test-npm-package.mjs` updated for the new layout.

---

## 8. Sequencing

1. csproj publish properties; verify one RID by hand, check size, run E2E.
2. Rewrite `cli.js` against that one hand-built binary.
3. Generate the platform-package scaffolding (a script, not six hand-written
   directories — they must stay version-locked).
4. Extend `release.sh` with the matrix and the publish ordering.
5. Clean-machine verification per §7.
6. Update `README.md` (drop the ".NET 8 and Node.js are required" line → Node
   only), `CLAUDE.md` (release + dev-install sections), and
   `skills/effortless-cli/SKILL.md` in the `effortless-skills` repo (its
   prerequisites block still instructs `dotnet --version # need >= 8.0` and
   "Stop and surface: `dotnet` is missing").

Steps 1–2 are independently testable and reversible. Nothing is published until
step 4.

---

## 9. What this does not do

This removes the .NET dependency **for users**, not for the project. The
runtime is bundled into the shipped binary rather than eliminated — you still
write C#, still build with the .NET SDK locally and in CI. That is a
relocation of the dependency from the user's machine to the build, which is
the entire point, but it is not "the CLI is no longer a .NET program."

Fully eliminating .NET would mean porting ~20k lines of HTTP-and-file-I/O C# to
Node. Feasible — the shim and `lib/fileset-handler.mjs` are already Node — but
it is weeks of rewriting working code, including the fiddly parts (`ZfsLedger`,
`FileSetCleaner`, `SeedReplacements`). Not recommended as a means of escaping
the dependency, since self-contained publishing already achieves that for every
user. Only worth doing if single-runtime maintenance becomes a goal in itself.
