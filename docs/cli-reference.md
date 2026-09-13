<!-- Generated from effortless-rulebook.json. Do not edit. -->
# Effortless CLI reference

## Help syntax

```text
Syntax: effortless [account/]transpiler [Options]
```

## CLI meta

Options about the CLI itself: help, version, info, debug, self-upgrade.

### `-help`

- Aliases: `h`
- Bareword forms: `help`
- Value type: `bool`
- Help text: Show help about how to use the Effortless CLI
- Description: Print usage.

### `-debug`

- Aliases: None
- Bareword forms: None
- Value type: `bool`
- Help text: Show debug output
- Description: Verbose diagnostics.

### `-info`

- Aliases: None
- Bareword forms: `info`
- Value type: `bool`
- Help text: Show configured settings
- Description: Show CLI/user configuration.

### `-version`

- Aliases: `v`
- Bareword forms: `version`, `v`
- Value type: `bool`
- Help text: Show CLI version
- Description: Print the version.

### `-checkVersion`

- Aliases: `cv`, `uc`, `update`
- Bareword forms: None
- Value type: `bool`
- Help text: Check GitHub for a newer commit and offer to reinstall
- Description: Check for and optionally install a newer CLI build.

## Project file

Creating, describing and configuring the effortless.json project.

### `-init`

- Aliases: None
- Bareword forms: `init`
- Value type: `bool`
- Help text: Initialize the current folder as the root of an Effortless project. An Optional parameter of force will create a sub-project.
- Description: Create effortless.json, .gitignore, effortless.env, a starter rulebook, and register compile-rulebook against it, in cwd.

### `-describe`

- Aliases: `d`
- Bareword forms: `list`, `describe`
- Value type: `bool`
- Help text: Describe the transpilers in the current folder and its children
- Description: 

### `-describeAll`

- Aliases: `da`
- Bareword forms: `da`, `describeall`
- Value type: `bool`
- Help text: Describe all of the transpiler in the project
- Description: Print the whole project.

### `-listSettings`

- Aliases: `ls`
- Bareword forms: None
- Value type: `bool`
- Help text: List of project settings
- Description: Print ProjectSettings.

### `-addSetting`

- Aliases: `as`
- Bareword forms: None
- Value type: `List<string>`
- Help text: Adds a setting to the Effortless Project
- Description: Add/replace a ProjectSetting.

### `-removeSetting`

- Aliases: `rs`
- Bareword forms: None
- Value type: `List<string>`
- Help text: Removes a setting from the Effortless Project
- Description: Remove a ProjectSetting.

### `-projectName`

- Aliases: `name`
- Bareword forms: None
- Value type: `string`
- Help text: Name of the project (optional parameter to the init command)
- Description: Project name for -init.

### `-describeLocal`

- Aliases: `dl`
- Bareword forms: `describelocal`
- Value type: `bool`
- Help text: Describe only the transpilers installed in the current folder
- Description: Print project summary for steps registered exactly at cwd.

### `-describeWithSubprojects`

- Aliases: `dws`
- Bareword forms: `describewithsubprojects`
- Value type: `bool`
- Help text: Describe the whole project, including nested effortless projects normally excluded
- Description: Print the whole project, including nested effortless projects.

## Install / uninstall tools

Registering transpiler steps in effortless.json.

### `-install`

- Aliases: None
- Bareword forms: `install`
- Value type: `bool`
- Help text: Saves the current command into the Effortless Project file
- Description: Register a transpiler step in effortless.json (and run it once).

### `-uninstall`

- Aliases: None
- Bareword forms: `uninstall`
- Value type: `bool`
- Help text: Removes the current command from the Effortless Project file
- Description: Remove a registered step.

### `-execute`

- Aliases: `exec`
- Bareword forms: None
- Value type: `string`
- Help text: Executes the given command as a ProcessInfo.Start
- Description: Run a local command as a (registered) step.

### `-transpilerGroup`

- Aliases: `tg`
- Bareword forms: None
- Value type: `string`
- Help text: Name of a group to put a transpiler in within a specific folder
- Description: Group tag for a step.

### `-disable`

- Aliases: None
- Bareword forms: `disable`
- Value type: `bool`
- Help text: Marks the current command's step as disabled in the effortless.json Project file
- Description: Mark a registered step disabled (skipped by build).

### `-enable`

- Aliases: None
- Bareword forms: `enable`
- Value type: `bool`
- Help text: Marks the current command's step as enabled in the effortless.json Project file
- Description: Mark a registered step enabled (included in build).

## Build

Running registered transpiler steps.

### `-build`

- Aliases: `b`, `replay`, `rebuild`, `pull`
- Bareword forms: `build`, `rebuild`, `pull`
- Value type: `bool`
- Help text: Build any transpilers in the current folder (or children).
- Description: Run every enabled step at or below cwd.

### `-buildAll`

- Aliases: `ba`, `replayall`, `rebuildAll`, `pullAll`
- Bareword forms: `buildall`, `rebuildall`, `pullAll`
- Value type: `bool`
- Help text: Builds all transpilers in the project
- Description: Run every step in the whole project, excluding nested effortless projects.

### `-buildLocal`

- Aliases: `bl`, `replaylocal`, `rebuildLocal`, `pullLocal`
- Bareword forms: `buildlocal`
- Value type: `bool`
- Help text: Build only the transpilers installed in the current folder
- Description: Run only steps whose RelativePath equals cwd.

### `-buildOnTrigger`

- Aliases: `bot`
- Bareword forms: None
- Value type: `string`
- Help text: Builds whenever a trigger is invoked (see readme for URL)
- Description: 

### `-compileOnSave`

- Aliases: `cos`
- Bareword forms: `compileOnSave`, `cos`
- Value type: `string`
- Help text: Recompile one rulebook whenever it is saved
- Description: 

### `-buildOnSave`

- Aliases: `bos`
- Bareword forms: `buildOnSave`, `bos`
- Value type: `string`
- Help text: Build from this folder whenever a file is saved
- Description: 

### `-rebuildAllOnSave`

- Aliases: None
- Bareword forms: `rebuildAllOnSave`
- Value type: `string`
- Help text: Rebuild the whole project whenever a file is saved
- Description: 

### `-includeDisabled`

- Aliases: `id`
- Bareword forms: None
- Value type: `bool`
- Help text: Include disabled tools in the build
- Description: Build disabled steps as well.

### `-continueOnError`

- Aliases: `coe`, `ignoreErrors`, `ignoreError`
- Bareword forms: None
- Value type: `bool`
- Help text: Don't let one failing step stop the build: run every remaining transpiler, write the full exception detail of anything that failed to errors.json in the project root, and still exit 0. Defaults to off.
- Description: Keep building after a failed step.

### `-buildWithSubprojects`

- Aliases: `bws`
- Bareword forms: `buildwithsubprojects`
- Value type: `bool`
- Help text: Build the whole project, including nested effortless projects normally excluded
- Description: Run every step in the whole project, including nested effortless projects.

## Clean

Removing generated output using the .zfs ledgers.

### `-clean`

- Aliases: `c`
- Bareword forms: `clean`
- Value type: `bool`
- Help text: Clean all transpilers installed in and downstream of this folder
- Description: Delete generated files recorded in .zfs ledgers.

### `-cleanAll`

- Aliases: `ca`
- Bareword forms: `cleanall`
- Value type: `bool`
- Help text: Clean all project transpilers
- Description: Clean the whole project, excluding nested effortless projects.

### `-cleanLocal`

- Aliases: `cl`
- Bareword forms: `cleanlocal`
- Value type: `bool`
- Help text: Cleans only transpilers installed in the current folder
- Description: Clean only steps registered exactly at cwd.

### `-purge`

- Aliases: None
- Bareword forms: None
- Value type: `bool`
- Help text: When supplied with clean, runs clean on all orphaned transpilers
- Description: Also clean ledgers of steps that are no longer registered.

### `-preserveZFS`

- Aliases: `rz`
- Bareword forms: None
- Value type: `bool`
- Help text: Determines if the input should be preserved.
- Description: Keep .zfs ledgers when cleaning.

### `-cleanWithSubprojects`

- Aliases: `cws`
- Bareword forms: `cleanwithsubprojects`
- Value type: `bool`
- Help text: Clean the whole project, including nested effortless projects normally excluded
- Description: Clean the whole project, including nested effortless projects.

## Transpile inputs & outputs

Options that shape a single transpile request: inputs, output name, parameters, account credentials, timeout, target URL.

### `-input`

- Aliases: `i`
- Bareword forms: None
- Value type: `List<string>`
- Help text: Input filename or comma separated list of file names
- Description: Input file(s) sent to the tool.

### `-output`

- Aliases: `o`
- Bareword forms: None
- Value type: `string`
- Help text: Output filename
- Description: Requested output filename for the tool.

### `-skipClean`

- Aliases: `sc`
- Bareword forms: None
- Value type: `bool`
- Help text: Don't clean the output before cooking
- Description: Write outputs without first cleaning the previous run.

### `-account`

- Aliases: `a`
- Bareword forms: None
- Value type: `string`
- Help text: The account which the transpiler belongs to
- Description: Credential/account selector for a tool.

### `-parameters`

- Aliases: `p`
- Bareword forms: None
- Value type: `List<string>`
- Help text: A list of parameters
- Description: key=value parameters for the tool.

### `-waitTimeout`

- Aliases: `w`
- Bareword forms: None
- Value type: `int`
- Help text: The amount of time to wait for the command to continue
- Description: Timeout in milliseconds.

### `-targetUrl`

- Aliases: `g`
- Bareword forms: None
- Value type: `string`
- Help text: TargetUrl of the tool being invoked
- Description: POST directly to a tool URL.

## Tool resolution & versions

How a tool name becomes a URL: remote index, versions, pins, upgrades.

### `-listVersions`

- Aliases: `lv`, `list`, `l`
- Bareword forms: None
- Value type: `bool`
- Help text: List all available versions of the tool
- Description: List versions of a tool from the remote index.

### `-refreshTools`

- Aliases: `rt`
- Bareword forms: `refreshtools`
- Value type: `bool`
- Help text: Purge and re-fetch the remote tools index
- Description: Force a re-download of the tools index.

### `-listTools`

- Aliases: `tools`, `lt`
- Bareword forms: `listtools`, `tools`, `lt`
- Value type: `bool`
- Help text: List all available tools in the current remote tools catalog
- Description: List canonical remote tool names and their current head versions.

### `-searchTools`

- Aliases: `search`, `st`
- Bareword forms: `searchtools`, `search`
- Value type: `string`
- Help text: Search the current remote tools catalog by name, description, or tags
- Description: Find tools by canonical or short name without guessing an exact tool name.

### `-upgrade`

- Aliases: `up`
- Bareword forms: `upgrade`, `unpin`
- Value type: `bool`
- Help text: Update the pinned version of this tool to the current head version (does not run the tool)
- Description: Unpin a tool so it tracks HEAD.

### `-upgradeAll`

- Aliases: `ua`
- Bareword forms: `upgradeall`
- Value type: `bool`
- Help text: Upgrade all transpilers in the project to the latest version
- Description: Unpin every tool.

### `-pin`

- Aliases: None
- Bareword forms: `pin`
- Value type: `string`
- Help text: Pin this project's step to a specific catalog version or URL
- Description: Pin an installed step to a version or URL for this project.

### `-category`

- Aliases: `cat`
- Bareword forms: None
- Value type: `string`
- Help text: Filter the tool catalog by category (the middle segment of account/category/tool)
- Description: Filter listTools/searchTools by category

### `-updatedSince`

- Aliases: `since`
- Bareword forms: None
- Value type: `string`
- Help text: Only tools whose head version was created on or after yyyy-mm-dd
- Description: Only tools with a head newer than a date

### `-headOnly`

- Aliases: `head`
- Bareword forms: None
- Value type: `bool`
- Help text: Only tools that currently have a head version
- Description: Hide tools with NO HEAD

### `-requiresKey`

- Aliases: `rk`
- Bareword forms: None
- Value type: `string`
- Help text: Filter by whether the tool requires an account API key: true or false
- Description: Filter by API-key requirement (true|false)

### `-sort`

- Aliases: None
- Bareword forms: None
- Value type: `string`
- Help text: Sort the tool list: name (default), updated, or popular
- Description: Sort by name, updated, or popular

### `-json`

- Aliases: None
- Bareword forms: None
- Value type: `bool`
- Help text: Emit the filtered tool list as JSON instead of a table
- Description: Emit listTools/searchTools output as JSON

## Tool URL overrides

The per-user ~/.effortless/tool_urls.json override table.

### `-listToolUrls`

- Aliases: `lu`, `listUrls`
- Bareword forms: `listtoolurls`, `listurls`, `lu`
- Value type: `bool`
- Help text: List all custom tool urls defined for this user
- Description: List URL overrides.

### `-viewToolUrl`

- Aliases: `vu`, `vt`, `viewUrl`
- Bareword forms: `viewtoolurl`, `viewurl`, `vu`, `vt`
- Value type: `string`
- Help text: View the url for the specified tool
- Description: Show one URL override.

### `-setToolUrl`

- Aliases: `su`, `setUrl`
- Bareword forms: `settoolurl`, `seturl`, `su`, `st`
- Value type: `string`
- Help text: Set a tool's URL to a custom endpoint for this user
- Description: Add/replace a URL override.

### `-removeToolUrl`

- Aliases: `ru`, `removeUrl`
- Bareword forms: `removetoolurl`, `removeurl`, `ru`, `rt`
- Value type: `string`
- Help text: Remove a custom tool URL from this user's config, setting it back to the default value.
- Description: Remove a URL override.

## Authentication & account

Magic-link login, project login, subscription, API keys.

### `-runAs`

- Aliases: `ra`
- Bareword forms: None
- Value type: `string`
- Help text: Run as this user (look for this user's key file)
- Description: Use an alternate key file.

### `-setAccountAPIKey`

- Aliases: `api`, `setAccountKey`
- Bareword forms: None
- Value type: `string`
- Help text: Add an account api key
- Description: Store an API key for -account.

### `-login`

- Aliases: `auth`, `authenticate`
- Bareword forms: `auth`, `login`, `authenticate`
- Value type: `bool`
- Help text: Sign in to EffortlessAPI (preview: the service does not enforce accounts yet)
- Description: Email magic-link login.

### `-projectLogin`

- Aliases: `projectAuth`
- Bareword forms: `projectlogin`, `projectauth`
- Value type: `bool`
- Help text: Sign this project in to EffortlessAPI (preview: nothing is enforced yet)
- Description: Project-scoped login.

### `-logout`

- Aliases: `signout`
- Bareword forms: `logout`, `signout`
- Value type: `bool`
- Help text: Logout of your cli user account
- Description: Clear the global token.

### `-plan`

- Aliases: `subscription`
- Bareword forms: `subscription`, `plan`
- Value type: `bool`
- Help text: Show the EffortlessAPI plan for this account (preview plan until subscriptions exist)
- Description: Show the account plan.

## Seeds (start a project from a public repository)

An Effortless seed is a public GitHub repository with effortless.json at its root. listSeeds finds them across an ordered list of GitHub accounts (the seed sources: ssotme and effortlessapi by default, managed with listSeedSources/addSeedSource/removeSeedSource); cloneSeed clones one, keeps its .git history, and never executes downloaded code.

### `-listSeeds`

- Aliases: `lsd`
- Bareword forms: `listseeds`
- Value type: `bool`
- Help text: Lists public Effortless seed repositories across the seed sources
- Description: List public Effortless seed repositories across the configured seed sources.

### `-cloneSeed`

- Aliases: `cs`, `clone`
- Bareword forms: `cloneseed`, `clone`
- Value type: `bool`
- Help text: Clones a public Effortless seed repository (account/repo, repo, or clone URL)
- Description: Clone a public Effortless seed repository.

### `-listSeedSources`

- Aliases: `lss`
- Bareword forms: `listseedsources`
- Value type: `bool`
- Help text: Lists the GitHub accounts searched for Effortless seeds
- Description: Lists the GitHub accounts searched for Effortless seeds.

### `-addSeedSource`

- Aliases: `ass`
- Bareword forms: `addseedsource`
- Value type: `string`
- Help text: Adds a GitHub account to the seed sources
- Description: Adds a GitHub account to the seed sources.

### `-removeSeedSource`

- Aliases: `rss`
- Bareword forms: `removeseedsource`
- Value type: `string`
- Help text: Removes a GitHub account from the seed sources
- Description: Removes a GitHub account from the seed sources.

## Local tools (effortless-tools/, serve)

Tools that live inside the project at effortless-tools/<name>/ and are hosted by the CLI itself over the same REST contract published tools speak. A build that names a local tool starts an ephemeral host for the duration of the build; serve keeps one running so repeated builds reuse it.

### `-serve`

- Aliases: None
- Bareword forms: `serve`
- Value type: `bool`
- Help text: Hosts this project's local tools (effortless-tools/) over HTTP
- Description: Run the local transpiler host resident so builds reuse one long-lived process.

### `-port`

- Aliases: None
- Bareword forms: None
- Value type: `int`
- Help text: Port for the local transpiler host (default: an ephemeral port)
- Description: Fixed port for serve.

## Exit codes

- `0` — **Command completed; transpile output written; management command finished.**: Includes -continueOnError builds that had failures (documented contract: still exit 0).
- `-1` — **A user-facing error was printed and the command did not complete.**: ProjectNotConfiguredException prints NOTHING before exiting -1.
- `-1` — **Unexpected exception; message printed in red then Environment.Exit(-1).**: Stack trace is not printed unless the exception surfaced through the TRANSPILER ERROR box path.
- `-1` — **A build step failed without -continueOnError: TranspilerStepFailedException propagates out of DoRebuild to Main.**: errors.json is still written by BuildErrorLog.Finish in the finally block before the exit.

## Configuration files

- `<root>/effortless.json` — The project: Name, SSoTmeProjectId, ProjectSettings, ProjectTranspilers (see ProjectFileFields). Indented JSON + trailing newline; custom non-managed properties on transpiler entries are preserved across saves (matched by Name+RelativePath+TranspilerGroup).
- `<root>/ssotme.json` — Older project file name; renamed to effortless.json when found. Also accepted as-is if the rename does not happen (e.g. effortless.json exists but is invalid).
- `<root>/aicapture.json and <root>/SSoTmeProject.json` — Older project file names; renamed to effortless.json when found. Not renamed; loaded in place and saved back under the SAME name (GetProjectFI chooses the first valid candidate).
- `<root>/effortless.env` — Project-scoped credentials: EFFORTLESS_JWT and {ACCOUNT}_{PAT|API_KEY|APIKEY|KEY|BASEID|BASE_ID}. A ssotme.env from an older install is auto-renamed to effortless.env when the latter is absent. Comment lines start with #; surrounding quotes are stripped; keys are case-insensitive.
- `<root>/.gitignore` — Created by init with the standard ignore list; existing files get "effortless.env" appended when missing. Template lines: /**/obj/**/*, /**/bin/**/*, /**/.effortless/**/*, /**/.ssotme/**/*, /**/.vs/**/*, /**/node_modules/**/*, /**/.vscode/**/*, ssotme.env, effortless.env. The .ssotme and ssotme.env lines keep clones made with older releases clean.
- `<root>/effortless-rulebook/effortless-rulebook.json` — Seed rulebook {"project":{"name":"<name>"}}.
- `<root>/errors.json` — Per-build failure ledger (schema effortless-build-errors/v1, camelCase). Top-level: schema, generatedAt, startedAt, projectRoot, buildCommand, continueOnError, totalSteps, succeededSteps, failedSteps, skippedSteps, failedStepNames[], steps[] (name, relativePath, status, message, finishedAt), errors[] (StepRecord with exitCode, resolvedVersion, resolvedUrl, transpilerException, cliException chains).
- `<root>/.effortless/<RelativePath>/<toolKey>.zfs` — GZip of the FileSet XML a step last produced; Never entries carry the CLI's CleanIfUnchanged stamp, self-source entries carry SkipClean=true. toolKey = sanitized POST URL for URL tools, else LowerHyphenName(Name). .effortless is created with the Hidden attribute.
- `<root>/.effortless/<RelativePath>/<toolKey>.xml` — Uncompressed copy of the ledger written with -debug. Deleted by clean alongside the .zfs.
- `<root>/.effortless/tempFileSet_<guid>.xml` — Transient copy of the output XML during SaveFileSet. Deleted immediately after extraction.
- `~/.effortless/effortless.key (or effortless.<runAs>.key)` — { EmailAddress, Secret, APIKeys: { account: key } }. An empty key is returned when the file is missing. Migrated from ~/.ssotme/ssotme.key and ssotme.<runAs>.key by UserConfigMigration on first run.
- `~/.effortless/tool_urls.json` — { "<tool>": "<url>" } overrides plus the managed cli-cloud-bridge entry. Indented JSON.
- `~/.effortless/update_check.json` — { Preference: "always"|"never", LastCheckedDate: "yyyy-MM-dd", PendingNotice: string|null }. Preference is unset/absent until the checkVersion Yes/No/Always/Never prompt is answered Always or Never; No/Yes leave it unset. NEW. Best-effort like tool_urls.json: a missing or corrupt file is treated as no preference set and no action taken, never a hard error.
- `~/.effortless/remote_tools/effortless-tools.json` — Cached cli-cloud-bridge list output: transpilerVersions{ "<acct>/<pkg>/<tool>": { "vX": { urls.post, metaData.isHeadVersion, ... } } }, cliUpdateAvailable, latestBridgeVersion, latestCliVersion, fetchedAt, totalCount. fetchedAt is a CLI-stamped UTC instant proving when a valid response last replaced the cache. Both "transpilerVersions" and "transpilers" keys are accepted. fetchedAt advances only after parse and structural validation succeed; refresh failure never makes stale data look current. Migrated from remote_tools/ssotme-tools.json on first run.
- `~/.effortless/remote_tools/cli_version` — CLI_VERSION at the time of the last refresh. Not by itself a refresh trigger.
- `~/.effortless/remote_tools/effortless.json` — Minimal project so the internal bridge run has a project context. Name "remote_tools". Only effortless.json is written here; an older remote_tools/ssotme.json is not migrated.
- `~/.effortless/bridge_version_index` — Highest latestBridgeVersion.versionIndex applied so far.
- `~/.effortless/update_available.json` — cliUpdateAvailable object from the last refresh (name, version, installLinks{windows,windowsArm,mac,macArm}). Still maintained; the banner that reads it is off by default.
- `~/.effortless/effortlessapi_token.txt` — Global JWT.
- `~/.effortless/effortlessapi_token_info.json` — { Token, Email, CreatedAt, ExpiresAt(+24h) }.
- `~/.effortless/seed_cache/<seed>/cache/**` — Files copied into a cloned seed. v2 does NOT apply it restored discovery, cloning and $key$ replacement only); the directory is still migrated by Open item recorded in ; implement or drop with an owner decision.
- `<root>/effortless-seed.json (ssotme-seed.json from older seeds is also read; + seed-config-values.json, seed-secret-values.json)` — Seed replacement tokens ($key$) and interactive answers. Retained explicit $key$ replacement contract: effortless-seed.json lists { key, description, default?, secret? Airtable schema guessing is removed.
- `~/.ssotme/` — User state directory of releases before 2026-09; copied to ~/.effortless on first run. copied (never moved) to ~/.effortless with renames: ssotme.key -> effortless.key, ssotme.<runAs>.key -> effortless.<runAs>.key, remote_tools/ssotme-tools.json -> remote_tools/effortless-tools.json, remote_tools/ssotme.json dropped, everything else same relative path. If both directories exist, ~/.effortless wins and ~/.ssotme is never read again (no merging). Migration failure is fatal.
- `~/.ssotme/MIGRATED-TO-EFFORTLESS` — Records that ~/.ssotme was copied to ~/.effortless. One line: target path, UTC timestamp, CLI version. The rest of ~/.ssotme is left untouched so an older ssotme binary keeps working.
- `<root>/.ssotme/` — Older per-project ledger directory; renamed to .effortless on project load. renamed to <root>/.effortless when .effortless is absent. It is gitignored build state so a rename is safe. init writes both ignore lines so old clones stay clean.
- `<root>/effortless-tools/<name>/tool.json` — Declares one local tool: { name, runtime: dotnet|node|script, entry, description, tags }. Optional: when absent a *.csproj means dotnet, package.json means node, and a single transpiler.* file means script. The folder name is the tool name and must be a catalog-style lower-hyphen name ([a-z0-9][a-z0-9-]*); a name in tool.json that disagrees with the folder is an error. Runtime contract: dotnet/node tools are started as child processes with PORT (and EFFORTLESS_TOOL_PORT) set and proxied at http://127.0.0.1:<PORT>/ exactly like a cloud workload; script tools get the EFFORTLESS_* directory contract.
- `<root>/.effortless/serve.json` — { port, pid, startedAt } of the resident local transpiler host started by effortless serve. A stale file (dead pid or nothing listening) is ignored and overwritten by the next serve; an ephemeral host never writes it.
- `~/.effortless/seed_sources.json` — { "sources": ["ssotme", "effortlessapi", ...] }: the ordered GitHub accounts searched by listSeeds and cloneSeed. Absent file = the defaults [ssotme, effortlessapi]. EFFORTLESS_SEED_GITHUB_ACCOUNT is prepended for one invocation without being written.
- `$EFFORTLESS_OUTPUT_DIR/effortless-overwrite-modes.json` — A script-shape local tool's per-file overwrite declaration: { "<relative path or glob>": "Always" | "Never", ... } using exactly the FileSet OverwriteMode values; first matching key wins; the file itself is never shipped. This is how a script says what a cloud tool says with fileSet.AddFile(name, contents, "Always"|"Never"). Any other value is a tool error; the CLI never guesses an overwrite mode. Node and dotnet shapes declare per file in their own response.

## Environment variables and keys

- `EFFORTLESS_CHILD_PROCESS` (process-env) — Set to "1" by the CLI around spawned child "effortless -buildLocal" / "-clean" processes; read by the once-daily background update check (E00-update-check-background) and its next-command notice (E01-update-check-notice) to skip both in children. Set via Environment.SetEnvironmentVariable on the PARENT process (inherited by the child) and cleared afterwards. The original CheckForUpdateNotice this once guarded was removed 2026-04-25 for making a blocking synchronous HTTP call on every invocation; E00/E01 are its async, non-blocking, once-per-day replacement.
- `HOME / USERPROFILE` (process-env) — Resolves ~/.effortless through Environment.GetFolderPath(SpecialFolder.UserProfile). Black-box tests override both to sandbox all user-home state.
- `PATH` (process-env) — Used to locate npm (checkVersion's reinstall step) and by spawned shells to find "effortless" for nested/sub-project builds. New build: child builds spawn the SAME executable by absolute path (Environment.ProcessPath / dotnet + dll) instead of relying on a globally installed "effortless" being on PATH. Console output of the child is unchanged.
- `EFFORTLESS_JWT` (effortless.env) — Project-scoped JWT written by "effortless projectLogin"; takes precedence over the global token and is sent as cliJwt. Read via EnvFile.TryLoadFromNearestProject; quotes stripped; comments (#) ignored.
- `{ACCOUNT}_PAT | _API_KEY | _APIKEY | _KEY` (effortless.env) — With -account X, the first matching X_{suffix} value is injected as apiKey=... (case-insensitive key lookup). Precedence: effortless.env beats ~/.effortless/effortless.key APIKeys[X]; both beat a same-named ProjectSetting.
- `{ACCOUNT}_BASEID | _BASE_ID` (effortless.env) — With -account X, injected as baseId=... Same rules as the apiKey mapping.
- `EFFORTLESS_SEED_GITHUB_ACCOUNT` (process-env) — Extra GitHub account searched first by listSeeds and cloneSeed for this invocation only; prepended to the seed sources without being written to seed_sources.json. The defaults are ssotme and effortlessapi from seed_sources.json; this variable is a per-invocation prepend.
- `EFFORTLESS_INPUT_DIR` (child-env (set by LocalToolHost for script tools)) — Directory where the host unzipped the request's input fileset (files at their RelativePath) before invoking a script tool. script-runtime contract. The entry runs with the tool folder as cwd via its interpreter (.sh=sh, .py=python3, .js/.mjs=node, .ps1=pwsh, .rb=ruby, else directly); stdout lines become message-level tool log entries and stderr lines error-level (warning-level on exit 0); a non-zero exit code fails the step; the run is bounded by cliWaitTimeout.
- `EFFORTLESS_OUTPUT_DIR` (child-env (set by LocalToolHost for script tools)) — Empty directory the script tool writes its outputs into; after exit every file under it becomes a FileSet entry at its relative path. Overwrite semantics are the protocol's, untouched: a file carries an OverwriteMode element only when the script declared one in effortless-overwrite-modes.json at the root of this directory; an undeclared file carries no node and is therefore written once and never overwritten, exactly like any other tool's file. script-runtime contract. The entry runs with the tool folder as cwd via its interpreter (.sh=sh, .py=python3, .js/.mjs=node, .ps1=pwsh, .rb=ruby, else directly); stdout lines become message-level tool log entries and stderr lines error-level (warning-level on exit 0); a non-zero exit code fails the step; the run is bounded by cliWaitTimeout.
- `EFFORTLESS_OUTPUT_NAME` (child-env (set by LocalToolHost for script tools)) — The -output value (cliOutput) verbatim; the tool decides whether it names a file or a directory (D21). Empty when -output was not given. script-runtime contract. The entry runs with the tool folder as cwd via its interpreter (.sh=sh, .py=python3, .js/.mjs=node, .ps1=pwsh, .rb=ruby, else directly); stdout lines become message-level tool log entries and stderr lines error-level (warning-level on exit 0); a non-zero exit code fails the step; the run is bounded by cliWaitTimeout.
- `EFFORTLESS_PARAMS` (child-env (set by LocalToolHost for script tools)) — JSON array of the cliParams strings exactly as sent (e.g. ["schema=public","param1=x"]). script-runtime contract. The entry runs with the tool folder as cwd via its interpreter (.sh=sh, .py=python3, .js/.mjs=node, .ps1=pwsh, .rb=ruby, else directly); stdout lines become message-level tool log entries and stderr lines error-level (warning-level on exit 0); a non-zero exit code fails the step; the run is bounded by cliWaitTimeout.
- `EFFORTLESS_TOOL_NAME` (child-env (set by LocalToolHost for script tools)) — The local tool's name. script-runtime contract. The entry runs with the tool folder as cwd via its interpreter (.sh=sh, .py=python3, .js/.mjs=node, .ps1=pwsh, .rb=ruby, else directly); stdout lines become message-level tool log entries and stderr lines error-level (warning-level on exit 0); a non-zero exit code fails the step; the run is bounded by cliWaitTimeout.
- `PORT / EFFORTLESS_TOOL_PORT` (child-env (set by LocalToolHost for dotnet and node tools)) — Loopback port the dotnet/node tool process must listen on; the host proxies POST / to it. PORT is what CLIClassLibrary.StartToolListener already reads, so a local dotnet tool folder is literally a cloud workload run locally. proxied-runtime contract. Node tools also receive EFFORTLESS_FILESET_HANDLER: the absolute path of the fileset handler module (lib/fileset-handler.mjs, embedded in the CLI and extracted to <root>/.effortless/local-tools/) so the tool can import it without an npm install.
- `EFFORTLESS_SEED_GITHUB_API / EFFORTLESS_SEED_GITHUB_RAW` (process-env (test seam)) — Base URLs replacing https://api.github.com and https://raw.githubusercontent.com in SeedCatalogClient so the E2E suite can run a mock GitHub. Unset in normal use.
- `EFFORTLESS_SERVE_EXIT_AFTER_MS` (process-env (test seam)) — Makes effortless serve exit on its own after this many milliseconds instead of waiting for Ctrl+C. Unset in normal use; the exit path is the same one SIGINT/SIGTERM take (serve.json removed, host disposed).
- `EFFORTLESS_CLI_TEST_UTC_NOW` (process-env (test seam)) — Fixed UTC "now" for catalog-age and freshness decisions so the E2E suite is deterministic. /11. Unset in normal use.
