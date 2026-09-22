using Effortless.Cli.Auth;
using Effortless.Cli.Config;
using Effortless.Cli.FileSets;
using Effortless.Cli.LocalTools;
using Effortless.Cli.Options;
using Effortless.Cli.Project;
using Effortless.Cli.Updates;

namespace Effortless.Cli.Commands;

public sealed class CommandDispatcher
{
    private readonly CliArgumentParser _parser;
    private readonly RemoteToolsIndex _remoteTools;
    private readonly ToolResolver _toolResolver;
    private readonly CredentialResolver _credentialResolver;
    private readonly ProjectCommands _projectCommands;
    private readonly ToolUrlCommands _toolUrlCommands;
    private readonly VersionCommands _versionCommands;
    private readonly AuthCommands _authCommands;
    private readonly InfoCommand _infoCommand;
    private readonly UpdateChecker _updateChecker;
    private readonly ExecuteCommand _executeCommand;
    private readonly SeedCommands _seedCommands;
    private readonly LocalToolResolver _localTools;
    private readonly ProjectToolFreshness _projectToolFreshness;
    private readonly TimeProvider _timeProvider;
    private bool _projectCatalogChecked;
    private DateTimeOffset? _forcedRefreshAt;
    private bool _longRunning;

    /// <summary>
    /// R11 bound for long-running modes (buildOnTrigger, serve): at most one
    /// forced catalog refresh per this interval. One-shot commands get one.
    /// </summary>
    public static readonly TimeSpan ForcedRefreshInterval = TimeSpan.FromMinutes(10);

    public CommandDispatcher()
        : this(CreateTimeProvider())
    {
    }

    internal CommandDispatcher(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _parser = new CliArgumentParser();
        _remoteTools = new RemoteToolsIndex(
            refreshRunner: request =>
                new CloudBridgeClient(RunBridgeCommandLine)
                    .Refresh(request),
            timeProvider: timeProvider);
        _localTools = new LocalToolResolver();
        _toolResolver = new ToolResolver(_remoteTools, _localTools);
        _projectToolFreshness =
            new ProjectToolFreshness(_remoteTools);
        _credentialResolver = new CredentialResolver();
        _projectCommands = new ProjectCommands();
        _toolUrlCommands = new ToolUrlCommands();
        _versionCommands = new VersionCommands(_remoteTools);
        _authCommands = new AuthCommands(
            new MagicLinkAuth(
                () => ResolveAuthToolUrl(offline: false),
                () => ResolveAuthToolUrl(offline: true)));
        _infoCommand = new InfoCommand(_remoteTools);
        _updateChecker = new UpdateChecker();
        _executeCommand = new ExecuteCommand();
        _seedCommands = new SeedCommands();
    }

    public int Run(string[] args)
    {
        _projectCatalogChecked = false;
        try
        {
            return RunInvocation(
                _parser.Parse(args),
                activeStep: null);
        }
        finally
        {
            // S14b: an ephemeral local tool host lives exactly as long as the
            // CLI invocation that needed it.
            _localTools.Dispose();
        }
    }

    public int RunCommandLine(
        string commandLine,
        EffortlessProject project,
        bool continueOnError,
        BuildErrorLog buildErrorLog)
    {
        var invocation = ParseBuildInvocation(
            commandLine,
            project,
            continueOnError);
        invocation.BuildErrorLog = buildErrorLog;
        var comparable = commandLine.EndsWith(
            " -debug",
            StringComparison.Ordinal)
            ? commandLine[..^" -debug".Length]
            : commandLine;
        var activeStep = project?.ProjectTranspilers
            .FirstOrDefault(
                step => string.Equals(
                    step.CommandLine,
                    comparable,
                    StringComparison.Ordinal));
        return RunInvocation(invocation, activeStep);
    }

    internal CliInvocation ParseBridgeInvocation(
        string commandLine,
        EffortlessProject project,
        bool continueOnError)
    {
        var invocation = ParseBuildInvocation(
            commandLine,
            project,
            continueOnError);
        invocation.SkipRemoteToolsLookup = true;
        invocation.SuppressVersionLabel = true;
        return invocation;
    }

    private int RunBridgeCommandLine(
        string commandLine,
        EffortlessProject project,
        bool continueOnError) =>
        RunInvocation(
            ParseBridgeInvocation(
                commandLine,
                project,
                continueOnError),
            activeStep: null);

    internal CliInvocation ParseBuildInvocation(
        string commandLine,
        EffortlessProject project,
        bool continueOnError)
    {
        var invocation = _parser.Parse(commandLine);
        invocation.Project = project;
        invocation.IsBuildOperation = true;
        invocation.ContinueOnError = continueOnError;
        invocation.Options.continueOnError = continueOnError;
        invocation.CurrentDirectory = Environment.CurrentDirectory;
        return invocation;
    }

    private int RunInvocation(
        CliInvocation invocation,
        ProjectTranspiler activeStep)
    {
        try
        {
            if (invocation.Options.debug)
            {
                Console.WriteLine("DEBUG OUTPUT ENABLED");
            }

            if (RequiresFreshCatalogBeforeResolution(invocation)
                && !EnsureCatalogFresh())
            {
                return -1;
            }

            // P01-P03: resolve the explicit URL or raw tool argument before
            // loading a project. Build-loop invocations already carry one.
            _toolResolver.Resolve(invocation);
            if (invocation.CatalogRefreshFailed)
            {
                WriteError(
                    $"ERROR: Remote tools index refresh failed: {_remoteTools.LastRefreshError}");
                return -1;
            }

            AddPositionalParameters(invocation);

            // P05-P10: management precedence.
            if (invocation.Options.help)
            {
                return new HelpCommand().Run(invocation);
            }

            if (invocation.Options.info)
            {
                return _infoCommand.Run(invocation);
            }

            if (invocation.Options.version)
            {
                Console.WriteLine(CliVersion.DisplayVersion);
                return 0;
            }

            if (invocation.Options.init)
            {
                invocation.Project = _projectCommands.Init(invocation);
                invocation.Options.build = true;
            }

            if (invocation.HasErrors)
            {
                Console.Write(invocation.ErrorText);
                return -1;
            }

            if (NeedsProjectLoad(invocation.Options)
                && invocation.Project is null)
            {
                if (string.IsNullOrEmpty(invocation.CurrentDirectory)
                    || !Directory.Exists(invocation.CurrentDirectory))
                {
                    WriteError(
                        "The current working directory no longer exists.");
                    return -1;
                }

                invocation.Project = ProjectLocator.TryToLoad(
                    new DirectoryInfo(invocation.CurrentDirectory),
                    updateCurrent: false);
                if (invocation.Project is null)
                {
                    WriteError(
                        "ERROR: No project found in this directory or any parent directory.");
                    Console.WriteLine();
                    WriteError(
                        "Run `effortless -init` to create a new project in this directory.");
                    return -1;
                }
            }

            if (invocation.Project is not null)
            {
                if (RequiresProjectCatalogGate(invocation)
                    && !EnsureProjectToolsCurrent(invocation))
                {
                    return -1;
                }

                PrepareProjectInvocation(invocation);
            }
            else if (RequiresProjectCatalogGate(invocation)
                     && !EnsureProjectToolsCurrent(invocation))
            {
                return -1;
            }

            // T00: project token wins; otherwise use the global token as-is.
            if (!invocation.Options.login
                && !invocation.Options.projectLogin
                && !invocation.Options.logout
                && !invocation.SkipRemoteToolsLookup)
            {
                invocation.Jwt =
                    EnvFile.TryLoadFromNearestProject(
                            invocation.Options.debug)
                        ?.GetValue("EFFORTLESS_JWT");
                if (string.IsNullOrEmpty(invocation.Jwt))
                {
                    invocation.Jwt =
                        new JwtStore().GetStoredJWTToken();
                }
            }

            return Dispatch(invocation, activeStep);
        }
        finally
        {
            if (invocation.Project is not null
                && Directory.Exists(invocation.CurrentDirectory))
            {
                new EmptyFolderPruner(invocation.Project)
                    .RemoveEmptyFolders(
                        invocation.CurrentDirectory);
            }
        }
    }

    private void PrepareProjectInvocation(CliInvocation invocation)
    {
        if (invocation.Options.install
            && HasMissingInstallInput(invocation.Options.input))
        {
            Console.WriteLine(
                "Auto-building project before install because an input file is missing...");
            try
            {
                new BuildRunner(
                        invocation.Project,
                        RunCommandLine,
                        invocation.BuildErrorLog)
                    .Rebuild(
                        invocation.CurrentDirectory,
                        invocation.Options.includeDisabled,
                        invocation.Options.transpilerGroup,
                        isBuildLocal: false,
                        invocation.Options.debug);
                Console.WriteLine(
                    "Auto-build completed successfully.");
            }
            catch (Exception exception)
            {
                WriteColor(
                    $"WARNING: Auto-build failed but continuing with install: {exception.Message}",
                    ConsoleColor.Yellow);
            }
        }

        _credentialResolver.AddProjectSettings(invocation);
        var loader = new InputFileSetLoader(
            invocation.Options.input,
            invocation.Project);
        try
        {
            loader.LoadInputFiles();
            invocation.InputFileSet = loader.FileSet;
            invocation.InputFileSetXml = loader.InputFileSetXml;
            _credentialResolver.Resolve(invocation);
        }
        catch (Exception exception)
        {
            PrintTranspilerError(
                invocation,
                exception);
            invocation.SuppressTranspile = true;
        }
    }

    /// <summary>
    /// R11 (D29): a catalog-resolved tool that never answered gets one forced
    /// catalog refresh; if its head URL moved, the caller retries once against
    /// the new URL. -targetUrl, bare URLs, and tool_urls.json overrides never
    /// reach here because they are not catalog-resolved.
    /// </summary>
    private bool TryRecoverFromCompleteTimeout(
        CliInvocation invocation,
        TranspileClientResult result)
    {
        if (!invocation.IsCatalogResolved)
        {
            return false;
        }

        var now = _timeProvider.GetUtcNow();
        if (_forcedRefreshAt is { } last
            && (!_longRunning || now - last < ForcedRefreshInterval))
        {
            return false;
        }

        _forcedRefreshAt = now;
        var name = invocation.RawTranspilerArg ?? invocation.Transpiler;
        var previousUrl = invocation.TargetUrl;
        Console.WriteLine(
            $"[cli] {name} did not respond ({DescribeFailure(result.ConnectionFailure)}); refreshing the catalog to check for a newer build...");
        if (!_remoteTools.Refresh(
                "Catalog-resolved tool completely timed out",
                $"'{name}' at {previousUrl} failed with {DescribeFailure(result.ConnectionFailure)} before producing any response. R11 forces one catalog refresh (ignoring the 24h stamp) to learn whether the tool's head URL moved."))
        {
            WriteError(
                $"ERROR: Remote tools index refresh failed: {_remoteTools.LastRefreshError}");
            return false;
        }

        var newUrl = _toolResolver.ReResolveFromCatalog(invocation);
        if (string.IsNullOrWhiteSpace(newUrl))
        {
            invocation.TargetUrl = previousUrl;
            return false;
        }

        if (string.Equals(
                newUrl.TrimEnd('/'),
                previousUrl?.TrimEnd('/'),
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        Console.WriteLine(
            $"Tool {name} moved; retrying against {newUrl}");
        return true;
    }

    private static string DescribeFailure(TranspileConnectionFailure failure) =>
        TranspileConnectionFailureText.Describe(failure);

    /// <summary>
    /// Step 11: validates the listTools/searchTools modifiers. Bad values are
    /// explicit errors rather than silently ignored filters.
    /// </summary>
    internal static bool TryBuildCatalogQuery(
        CliOptions options,
        out CatalogQuery query,
        out string error)
    {
        query = null;
        error = null;

        DateTimeOffset? updatedSince = null;
        if (!string.IsNullOrWhiteSpace(options.updatedSince))
        {
            if (!DateTimeOffset.TryParseExact(
                    options.updatedSince.Trim(),
                    new[] { "yyyy-MM-dd", "yyyy-MM-ddTHH:mm:ssZ", "yyyy-MM-ddTHH:mm:ss.fffffffZ", "O" },
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal
                    | System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out var parsed))
            {
                error =
                    $"Invalid -updatedSince value '{options.updatedSince}'. Use yyyy-mm-dd (UTC).";
                return false;
            }

            updatedSince = parsed;
        }

        bool? requiresKey = null;
        if (!string.IsNullOrWhiteSpace(options.requiresKey))
        {
            if (!bool.TryParse(options.requiresKey.Trim(), out var flag))
            {
                error =
                    $"Invalid -requiresKey value '{options.requiresKey}'. Use true or false.";
                return false;
            }

            requiresKey = flag;
        }

        var sort = string.IsNullOrWhiteSpace(options.sort)
            ? CatalogQuery.SortByName
            : options.sort.Trim().ToLowerInvariant();
        if (!CatalogQuery.SortValues.Contains(sort))
        {
            error =
                $"Invalid -sort value '{options.sort}'. Use {string.Join(", ", CatalogQuery.SortValues)}.";
            return false;
        }

        query = new CatalogQuery(
            Text: options.searchTools,
            Category: options.category,
            Account: options.account,
            UpdatedSince: updatedSince,
            HeadOnly: options.headOnly,
            RequiresKey: requiresKey,
            Sort: sort);
        return true;
    }

    /// <summary>
    /// Step 13: the auth tool is an ordinary catalog tool. A tool_urls.json
    /// override wins (local dev), otherwise R0 freshness applies and the head
    /// version is used. Null means "not in the catalog".
    /// </summary>
    private string ResolveAuthToolUrl(bool offline)
    {
        // A tool_urls.json override (local dev: -setToolUrl effortless-auth=...)
        // wins by either name, exactly as it would for a build step.
        var overrideUrl = _remoteTools.TryGetToolUrl(MagicLinkAuth.AuthToolShortName)
            ?? _remoteTools.TryGetToolUrl(MagicLinkAuth.AuthToolName);
        if (!string.IsNullOrWhiteSpace(overrideUrl))
        {
            return overrideUrl;
        }

        if (offline)
        {
            return _remoteTools.Resolve(MagicLinkAuth.AuthToolName)?.Url;
        }

        var probe = new CliInvocation
        {
            Options = new CliOptions(),
            RawTranspilerArg = MagicLinkAuth.AuthToolName,
            CurrentDirectory = Environment.CurrentDirectory,
        };
        _toolResolver.Resolve(probe);
        if (probe.CatalogRefreshFailed)
        {
            throw new NoStackException(
                $"ERROR: Remote tools index refresh failed: {_remoteTools.LastRefreshError}");
        }

        return probe.TargetUrl;
    }

    private bool EnsureCatalogFresh()
    {
        if (_remoteTools.EnsureFresh())
        {
            return true;
        }

        WriteError(
            $"ERROR: Remote tools index refresh failed: {_remoteTools.LastRefreshError}");
        return false;
    }

    private bool EnsureProjectToolsCurrent(
        CliInvocation invocation)
    {
        if (_projectCatalogChecked)
        {
            return true;
        }

        if (!EnsureCatalogFresh())
        {
            return false;
        }

        var project = invocation.Project;
        if (project is null
            && !string.IsNullOrWhiteSpace(
                invocation.CurrentDirectory)
            && Directory.Exists(
                invocation.CurrentDirectory))
        {
            project = ProjectLocator.TryToLoad(
                new DirectoryInfo(
                    invocation.CurrentDirectory),
                updateCurrent: false);
        }

        if (project is null)
        {
            _projectCatalogChecked = true;
            return true;
        }

        var migrating = project.MigratedFromSsotme;
        var plan = _projectToolFreshness.Plan(
            project,
            migrating
                ? MissingProjectToolPolicy.Disable
                : MissingProjectToolPolicy.Fail);
        if (migrating)
        {
            // One-time legacy ssotme.json migration: steps whose tools are
            // gone were marked IsDisabled by Plan; record that and drop the flag.
            foreach (var entry in plan.Entries.Where(entry => entry.IsMissing))
            {
                Console.WriteLine(
                    $"[cli] Migrated ssotme.json: disabled step '{entry.Step.Name}' - tool '{entry.ToolName}' is not in the current catalog.");
            }

            project.MigratedFromSsotme = false;
            project.Save();
        }

        if (plan.Entries.Count == 0)
        {
            _projectCatalogChecked = true;
            return true;
        }

        Console.WriteLine(
            "[cli] Checking project tools against the current catalog...");
        if (!plan.IsSuccessful)
        {
            WriteError($"ERROR: {plan.Error}");
            ExplainMissingProjectTools(project, invocation, plan);
            if (!OfferToDisableMissingSteps(project, plan))
            {
                return false;
            }

            plan = _projectToolFreshness.Plan(
                project,
                MissingProjectToolPolicy.Fail);
            if (!plan.IsSuccessful)
            {
                WriteError($"ERROR: {plan.Error}");
                return false;
            }
        }

        // D17: the automatic gate advances unpinned steps to HEAD but never
        // silently discards a deliberate -pin. Only -upgrade/-upgradeAll unpin.
        plan.Apply(project, clearPins: false);
        _projectCatalogChecked = true;
        Console.WriteLine(
            "[cli] Project tools are current.");
        return true;
    }

    /// <summary>
    /// The catalog gate stopped the run: say which project file it read, which
    /// steps are broken, why that blocks even a one-off tool run, and how to fix it.
    /// </summary>
    private static void ExplainMissingProjectTools(
        EffortlessProject project,
        CliInvocation invocation,
        ProjectToolUpgradePlan plan)
    {
        var missing = plan.Entries.Where(entry => entry.IsMissing).ToList();
        var projectFile = Path.Combine(project.RootPath, "effortless.json");

        Console.WriteLine();
        Console.WriteLine(
            $"Nothing was run. The project this command belongs to has {missing.Count} step(s) whose tool is not in the current tool catalog.");
        Console.WriteLine();
        Console.WriteLine($"  Project file: {projectFile}");
        if (!string.Equals(
                Path.GetFullPath(project.RootPath).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(invocation.CurrentDirectory ?? project.RootPath).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.Ordinal))
        {
            Console.WriteLine(
                $"  (found by searching up from {invocation.CurrentDirectory} - the nearest folder with a project file)");
        }

        Console.WriteLine();
        Console.WriteLine("  Steps with a missing tool:");
        foreach (var entry in missing)
        {
            var folder = string.IsNullOrWhiteSpace(entry.Step.RelativePath)
                ? "/"
                : entry.Step.RelativePath;
            Console.WriteLine(
                $"    - '{entry.Step.Name}' in {folder}: {entry.Step.CommandLine}");
        }

        Console.WriteLine();
        Console.WriteLine(
            "Why: before running any tool - even a single one-off tool - the CLI checks every enabled");
        Console.WriteLine(
            "step in the project against the catalog and moves it to the latest version. A step whose");
        Console.WriteLine(
            "tool was renamed or retired (common in old ssotme.json projects) stops that check.");
        Console.WriteLine();
        Console.WriteLine("How to fix it (any one per step):");
        Console.WriteLine(
            "  - Disable the step: it stays in the file and build skips it. Set \"IsDisabled\": true on");
        Console.WriteLine(
            "    it in effortless.json, or run `effortless <tool> -disable` from that step's folder.");
        Console.WriteLine(
            "  - Remove the step: `effortless -uninstall <tool>` from that step's folder.");
        Console.WriteLine(
            "  - Point it at a tool you run yourself: `effortless -setToolUrl <tool>=<url>`.");
        Console.WriteLine(
            "  - Or run this command from a folder that has its own effortless.json (`effortless -init`).");
        Console.WriteLine();
    }

    /// <summary>
    /// Interactive terminals only: offer to disable every step whose tool is
    /// missing. Returns true when the steps were disabled and saved.
    /// </summary>
    private static bool OfferToDisableMissingSteps(
        EffortlessProject project,
        ProjectToolUpgradePlan plan)
    {
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            return false;
        }

        var missing = plan.Entries.Where(entry => entry.IsMissing).ToList();
        Console.Write(
            $"Disable these {missing.Count} step(s) in effortless.json now and continue? [y/N]: ");
        var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
        if (answer is not ("y" or "yes"))
        {
            Console.WriteLine("Nothing changed.");
            return false;
        }

        foreach (var entry in missing)
        {
            entry.Step.IsDisabled = true;
        }

        project.Save();
        Console.WriteLine(
            $"[cli] Disabled {missing.Count} step(s). Re-enable one with `effortless <tool> -enable` from its folder.");
        return true;
    }

    private int Dispatch(
        CliInvocation invocation,
        ProjectTranspiler activeStep)
    {
        var options = invocation.Options;
        if (options.login)
        {
            return _authCommands.Login();
        }

        if (options.projectLogin)
        {
            return _authCommands.ProjectLogin(invocation);
        }

        if (options.plan)
        {
            return _authCommands.Plan(
                invocation.Jwt
                ?? new JwtStore().GetStoredJWTToken());
        }

        if (options.logout)
        {
            return _authCommands.Logout();
        }

        if (options.describeLocal)
        {
            return _projectCommands.Describe(
                invocation,
                DescribeScope.Local);
        }

        if (options.describeWithSubprojects)
        {
            return _projectCommands.Describe(
                invocation,
                DescribeScope.WithSubprojects);
        }

        if (options.describeAll)
        {
            return _projectCommands.Describe(
                invocation,
                DescribeScope.All);
        }

        if (options.describe)
        {
            return _projectCommands.Describe(
                invocation,
                DescribeScope.Downstream);
        }

        if (options.listSettings)
        {
            return _projectCommands.ListSettings(invocation);
        }

        if (options.addSetting.Any())
        {
            return _projectCommands.AddSettings(invocation);
        }

        if (options.listSeeds)
        {
            return _seedCommands.List(invocation);
        }

        if (options.cloneSeed)
        {
            return _seedCommands.Clone(invocation);
        }

        if (options.listSeedSources)
        {
            return _seedCommands.ListSources();
        }

        if (!string.IsNullOrEmpty(options.addSeedSource))
        {
            return _seedCommands.AddSource(options.addSeedSource);
        }

        if (!string.IsNullOrEmpty(options.removeSeedSource))
        {
            return _seedCommands.RemoveSource(options.removeSeedSource);
        }

        if (!string.IsNullOrEmpty(options.viewToolUrl))
        {
            return _toolUrlCommands.View(options.viewToolUrl);
        }

        if (!string.IsNullOrEmpty(options.setToolUrl))
        {
            return _toolUrlCommands.Set(options.setToolUrl);
        }

        if (options.listToolUrls)
        {
            return _toolUrlCommands.List(options.debug);
        }

        if (!string.IsNullOrEmpty(options.removeToolUrl))
        {
            return _toolUrlCommands.Remove(options.removeToolUrl);
        }

        if (options.refreshTools)
        {
            return _versionCommands.Refresh(options.debug);
        }

        if (!string.IsNullOrEmpty(options.pin))
        {
            return _versionCommands.Pin(invocation, options.pin);
        }

        if (options.upgrade)
        {
            return _versionCommands.Upgrade(invocation, all: false);
        }

        if (options.upgradeAll)
        {
            return _versionCommands.Upgrade(invocation, all: true);
        }

        if (options.checkVersion)
        {
            return _updateChecker.RunExplicit();
        }

        if (options.listVersions)
        {
            return _versionCommands.ListVersions(
                invocation.RawTranspilerArg
                ?? invocation.Transpiler);
        }

        if (options.listTools
            || !string.IsNullOrEmpty(options.searchTools))
        {
            if (!TryBuildCatalogQuery(options, out var query, out var queryError))
            {
                WriteError(queryError);
                return -1;
            }

            return _versionCommands.ListTools(query, options.json);
        }

        if (options.removeSetting.Any())
        {
            return _projectCommands.RemoveSettings(invocation);
        }

        if (!string.IsNullOrEmpty(options.setAccountAPIKey))
        {
            return _authCommands.SetAccountApiKey(invocation);
        }

        if (invocation.SuppressTranspile)
        {
            return 0;
        }

        if (options.install
            && IsHttpUrl(invocation.RawTranspilerArg))
        {
            invocation.Project.Install(
                new TranspilePayload
                {
                    Transpiler = new Transpiler
                    {
                        Name = invocation.Transpiler,
                    },
                },
                options.transpilerGroup,
                pinnedVersion: null);
            return 0;
        }

        if (!string.IsNullOrEmpty(options.execute))
        {
            return _executeCommand.Run(invocation);
        }

        if (options.serve)
        {
            return new ServeCommand().Run(invocation);
        }

        // The save-watch modes are builds that keep running, so they dispatch
        // here whether or not an explicit build verb was also given: the flag
        // itself is the instruction.
        if (options.build
            || options.buildLocal
            || !string.IsNullOrWhiteSpace(options.compileOnSave)
            || !string.IsNullOrWhiteSpace(options.buildOnSave)
            || !string.IsNullOrWhiteSpace(options.rebuildAllOnSave))
        {
            _longRunning = !string.IsNullOrWhiteSpace(options.buildOnTrigger)
                || !string.IsNullOrWhiteSpace(options.compileOnSave)
                || !string.IsNullOrWhiteSpace(options.buildOnSave)
                || !string.IsNullOrWhiteSpace(options.rebuildAllOnSave);
            return new BuildCommand(RunCommandLine)
                .Run(invocation, all: false);
        }

        if (options.buildAll || options.buildWithSubprojects)
        {
            return new BuildCommand(RunCommandLine)
                .Run(
                    invocation,
                    all: true,
                    withSubprojects: options.buildWithSubprojects);
        }

        if (options.enable || options.disable)
        {
            return _projectCommands.SetStepDisabled(
                invocation,
                disabled: options.disable);
        }

        if (options.uninstall)
        {
            var name = invocation.RemainingArguments
                .FirstOrDefault();
            if (string.IsNullOrEmpty(name))
            {
                WriteError(
                    "Please specify a transpiler name to uninstall");
                return -1;
            }

            invocation.Project.Uninstall(
                name,
                options.transpilerGroup);
            return 0;
        }

        if ((options.clean
             || options.cleanLocal
             || options.cleanAll
             || options.cleanWithSubprojects)
            && !HasToolArgument(invocation))
        {
            return new CleanCommand().Run(invocation);
        }

        if (string.IsNullOrEmpty(invocation.TargetUrl))
        {
            if (invocation.HasExplicitVersionError)
            {
                return invocation.IsBuildOperation ? -1 : 0;
            }

            if (string.IsNullOrEmpty(
                    invocation.RawTranspilerArg))
            {
                WriteError(
                    "Missing argument name of transpiler");
                return -1;
            }

            PrintToolNotFound(
                invocation.RawTranspilerArg);
            return -1;
        }

        return Transpile(invocation, activeStep);
    }

    private int Transpile(
        CliInvocation invocation,
        ProjectTranspiler activeStep)
    {
        if (!invocation.SuppressVersionLabel
            && !string.IsNullOrEmpty(
                invocation.ResolvedVersionLabel))
        {
            WriteLabel(
                invocation.ResolvedVersionLabel);
        }

        using var client = new TranspileClient();
        var result = client.ExecuteAsync(invocation)
            .GetAwaiter()
            .GetResult();
        if (!result.Succeeded
            && result.IsCompleteTimeout
            && TryRecoverFromCompleteTimeout(invocation, result))
        {
            result = client.ExecuteAsync(invocation)
                .GetAwaiter()
                .GetResult();
        }

        if (!result.Succeeded)
        {
            if (result.IsCompleteTimeout)
            {
                var toolName = invocation.RawTranspilerArg ?? invocation.Transpiler;
                if (invocation.IsUserSetToolUrl)
                {
                    WriteError(
                        $"[cli] {toolName} is offline at {invocation.TargetUrl}. That URL is your tool_urls.json override, not the catalog"
                        + (string.IsNullOrEmpty(invocation.ResolvedVersionKey)
                            ? "."
                            : $" (catalog version {invocation.ResolvedVersionKey}).")
                        + $" Run 'effortless -removeToolUrl {toolName}' to use the catalog.");
                }
                else
                {
                    Console.WriteLine(
                        $"[cli] {toolName} is offline at {invocation.TargetUrl}; {_remoteTools.DescribeCatalogAge()}.");
                }
            }

            var exception = result.Payload.Exception;
            var isNotFound = exception.Message.Contains(
                "transpiler status message",
                StringComparison.OrdinalIgnoreCase)
                || exception.Message.Contains(
                    "Could not find transpiler",
                    StringComparison.OrdinalIgnoreCase);
            if (isNotFound)
            {
                PrintToolNotFound(invocation.Transpiler);
            }
            else
            {
                PrintTranspilerError(
                    invocation,
                    exception,
                    unreachable: result.IsCompleteTimeout);
            }

            if (activeStep is not null)
            {
                invocation.BuildErrorLog.RecordFailure(
                    activeStep,
                    -1,
                    exception,
                    null,
                    invocation.ResolvedVersionKey,
                    invocation.ResolvedVersionUrl);
                throw new TranspilerStepFailedException(
                    $"Transpiler '{activeStep.Name}' failed: {exception.Message}",
                    exception);
            }

            return -1;
        }

        var finalResult = 0;
        if (result.OutputDisposition
            == TranspileOutputDisposition.Clean)
        {
            ZfsLedger.CleanFileSet(
                invocation.Project,
                result.TranspilerKey,
                result.CurrentDirectory,
                result.Debug,
                deleteEmptyDirs: false,
                deleteUnchangedNever: true);
        }
        else if (result.OutputDisposition
                 == TranspileOutputDisposition.Save)
        {
            finalResult = ZfsLedger.SaveFileSet(
                invocation.Project,
                result.TranspilerKey,
                result.CurrentDirectory,
                result.ZippedOutputFileSet,
                result.InputFileSetXml,
                result.SkipClean,
                result.Debug);
            invocation.UpdateProject = true;
        }

        if (invocation.UpdateProject)
        {
            UpdateProject(
                invocation,
                activeStep,
                result.Payload);
        }

        return finalResult;
    }

    private static void UpdateProject(
        CliInvocation invocation,
        ProjectTranspiler activeStep,
        TranspilePayload payload)
    {
        if (invocation.IsBuildOperation
            && activeStep is not null)
        {
            invocation.Project.UpdateRuntimeOnly(
                activeStep,
                payload);
            SaveVersionHistoryIfChanged(invocation, activeStep);
            return;
        }

        if (invocation.Options.install)
        {
            invocation.Project.Install(
                payload,
                invocation.Options.transpilerGroup,
                ExplicitlyRequestedInstallVersion(invocation));
        }
        else if (activeStep is not null)
        {
            invocation.Project.Update(activeStep, payload);
            SaveVersionHistoryIfChanged(invocation, activeStep);
        }
    }

    private static string ExplicitlyRequestedInstallVersion(
        CliInvocation invocation)
    {
        var requestedTool = invocation.RawTranspilerArg;
        if (string.IsNullOrWhiteSpace(requestedTool)
            || string.IsNullOrWhiteSpace(
                invocation.ResolvedVersionKey))
        {
            return null;
        }

        var slash = requestedTool.LastIndexOf('/');
        if (slash < 0 || slash == requestedTool.Length - 1)
        {
            return null;
        }

        var suffix = requestedTool[(slash + 1)..];
        return suffix.Length > 1
            && suffix[0] is 'v' or 'V'
            && char.IsDigit(suffix[1])
                ? invocation.ResolvedVersionKey
                : null;
    }

    private static void SaveVersionHistoryIfChanged(
        CliInvocation invocation,
        ProjectTranspiler step)
    {
        if (string.IsNullOrEmpty(
                invocation.ResolvedVersionKey))
        {
            return;
        }

        var changed = false;
        if (!string.Equals(
                step.LastVersionUsed,
                invocation.ResolvedVersionKey,
                StringComparison.Ordinal))
        {
            step.LastVersionUsed =
                invocation.ResolvedVersionKey;
            changed = true;
        }

        if (!string.IsNullOrEmpty(
                invocation.ResolvedVersionUrl)
            && !string.Equals(
                step.LastUrl,
                invocation.ResolvedVersionUrl,
                StringComparison.Ordinal))
        {
            step.LastUrl =
                invocation.ResolvedVersionUrl;
            changed = true;
        }

        if (changed)
        {
            invocation.Project.Save();
        }
    }

    private static void AddPositionalParameters(
        CliInvocation invocation)
    {
        if (IsManagementOnly(invocation.Options))
        {
            return;
        }

        var arguments = invocation.RemainingArguments
            ?? new List<string>();
        var skip = arguments.Count > 0
                   && string.Equals(
                       arguments[0],
                       invocation.RawTranspilerArg,
                       StringComparison.Ordinal)
            ? 1
            : 0;
        var index = 1;
        foreach (var argument in arguments.Skip(skip))
        {
            invocation.Options.parameters.Add(
                $"param{index++}={argument}");
        }
    }

    private static bool NeedsProjectLoad(CliOptions options) =>
        !options.help
        && !options.info
        && !options.version
        && !options.login
        && !options.projectLogin
        && !options.plan
        && !options.logout
        && string.IsNullOrEmpty(options.setAccountAPIKey)
        && !options.listVersions
        && !options.refreshTools
        && !options.listTools
        && string.IsNullOrEmpty(options.searchTools)
        && !options.upgrade
        && !options.upgradeAll
        && !options.checkVersion
        && !options.listSeeds
        && !options.cloneSeed
        && !options.listSeedSources
        && string.IsNullOrEmpty(options.addSeedSource)
        && string.IsNullOrEmpty(options.removeSeedSource)
        && string.IsNullOrEmpty(options.viewToolUrl)
        && string.IsNullOrEmpty(options.setToolUrl)
        && !options.listToolUrls
        && string.IsNullOrEmpty(options.removeToolUrl);

    private bool RequiresFreshCatalogBeforeResolution(
        CliInvocation invocation)
    {
        var options = invocation.Options;
        if (invocation.SkipRemoteToolsLookup
            || IsCatalogOfflineCommand(options)
            || options.refreshTools
            || options.upgrade
            || options.upgradeAll
            || !string.IsNullOrWhiteSpace(
                options.targetUrl))
        {
            return false;
        }

        if (options.listVersions
            || options.listTools
            || !string.IsNullOrWhiteSpace(
                options.searchTools))
        {
            return true;
        }

        var rawName = invocation.RawTranspilerArg
                      ?? invocation.Transpiler
                      ?? invocation.RemainingArguments
                          ?.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(rawName)
            || IsHttpUrl(rawName)
            || !string.IsNullOrWhiteSpace(
                _remoteTools.TryGetToolUrl(rawName)))
        {
            return false;
        }

        // R12: a project-local tool never needs the catalog.
        return _localTools.Find(invocation, rawName) is null;
    }

    private static bool RequiresProjectCatalogGate(
        CliInvocation invocation)
    {
        var options = invocation.Options;
        if (invocation.SkipRemoteToolsLookup
            || IsCatalogOfflineCommand(options)
            || options.refreshTools
            || options.upgrade
            || options.upgradeAll
            || options.checkVersion
            || !string.IsNullOrWhiteSpace(
                options.targetUrl)
            || !string.IsNullOrWhiteSpace(
                options.execute)
            || options.enable
            || options.disable
            || !string.IsNullOrWhiteSpace(options.pin))
        {
            return false;
        }

        if (options.listVersions
            || options.listTools
            || !string.IsNullOrWhiteSpace(
                options.searchTools)
            || options.build
            || options.buildLocal
            || options.buildAll
            || options.buildWithSubprojects
            || options.install)
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(
            invocation.ResolvedToolName);
    }

    private static bool IsCatalogOfflineCommand(
        CliOptions options) =>
        options.help
        || options.info
        || options.version
        || options.login
        || options.projectLogin
        || options.plan
        || options.logout
        || options.describe
        || options.describeAll
        || options.describeLocal
        || options.describeWithSubprojects
        || options.listSeeds
        || options.cloneSeed
        || options.listSeedSources
        || !string.IsNullOrEmpty(options.addSeedSource)
        || !string.IsNullOrEmpty(options.removeSeedSource)
        || options.serve
        || options.listSettings
        || options.addSetting.Any()
        || options.removeSetting.Any()
        || !string.IsNullOrEmpty(
            options.setAccountAPIKey)
        || !string.IsNullOrEmpty(options.viewToolUrl)
        || !string.IsNullOrEmpty(options.setToolUrl)
        || options.listToolUrls
        || !string.IsNullOrEmpty(options.removeToolUrl)
        || options.clean
        || options.cleanLocal
        || options.cleanAll
        || options.cleanWithSubprojects
        || options.enable
        || options.disable
        || !string.IsNullOrEmpty(options.pin);

    private static bool IsManagementOnly(CliOptions options) =>
        options.help
        || options.info
        || options.version
        || options.init
        || options.login
        || options.projectLogin
        || options.plan
        || options.logout
        || options.describe
        || options.describeAll
        || options.describeLocal
        || options.describeWithSubprojects
        || options.listSettings
        || options.addSetting.Any()
        || options.removeSetting.Any()
        || !string.IsNullOrEmpty(options.setAccountAPIKey)
        || options.listVersions
        || options.refreshTools
        || options.listTools
        || !string.IsNullOrEmpty(options.searchTools)
        || options.upgrade
        || options.upgradeAll
        || options.checkVersion
        || options.listSeeds
        || options.cloneSeed
        || options.listSeedSources
        || !string.IsNullOrEmpty(options.addSeedSource)
        || !string.IsNullOrEmpty(options.removeSeedSource)
        || options.serve
        || !string.IsNullOrEmpty(options.viewToolUrl)
        || !string.IsNullOrEmpty(options.setToolUrl)
        || options.listToolUrls
        || !string.IsNullOrEmpty(options.removeToolUrl)
        || options.enable
        || options.disable
        || !string.IsNullOrEmpty(options.pin);

    private static bool HasToolArgument(
        CliInvocation invocation) =>
        !string.IsNullOrEmpty(
            invocation.RawTranspilerArg);

    private static bool HasMissingInstallInput(
        IEnumerable<string> inputs)
    {
        foreach (var inputValue in inputs
                     ?? Enumerable.Empty<string>())
        {
            foreach (var input in inputValue.Split(','))
            {
                var path = input.Contains('=')
                    ? input[(input.IndexOf('=') + 1)..]
                    : input;
                if (path.EndsWith('?'))
                {
                    continue;
                }

                var directory = Path.GetDirectoryName(path);
                var pattern = Path.GetFileName(path);
                var root = string.IsNullOrEmpty(directory)
                    ? new DirectoryInfo(
                        Environment.CurrentDirectory)
                    : new DirectoryInfo(
                        Path.Combine(
                            Environment.CurrentDirectory,
                            directory));
                if (!root.Exists
                    || root.GetFiles(pattern).Length == 0)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsHttpUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https";

    private static TimeProvider CreateTimeProvider()
    {
        var raw = Environment.GetEnvironmentVariable(
            "EFFORTLESS_CLI_TEST_UTC_NOW");
        return DateTimeOffset.TryParse(
            raw,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal
            | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out var value)
            ? new FixedTimeProvider(value)
            : TimeProvider.System;
    }

    private static void PrintToolNotFound(string toolName)
    {
        WriteError(
            $"\nERROR: Tool '{toolName}' does not exist.");
        WriteError(
            "\nThe tool was not found locally or on the tools server.");
        WriteError("\nTo see available tools, run:");
        WriteError("  effortless listTools");
        WriteError(
            "\nTo add a new tool URL mapping, run:");
        WriteError(
            "  effortless setToolUrl <tool-name>=<url>");
    }

    private static void PrintTranspilerError(
        CliInvocation invocation,
        Exception exception,
        bool unreachable = false)
    {
        if (invocation.SuppressTranspilerErrorOutput)
        {
            return;
        }

        // A tool that never answered received no input and hit no internal
        // error, so the "issue with the transpiler" box would misdirect.
        if (unreachable)
        {
            PrintExceptionChain(exception);
            return;
        }

        var toolName = invocation.RawTranspilerArg ?? invocation.Transpiler;
        WriteColor(
            "\n=======================================================",
            ConsoleColor.Yellow);
        WriteColor(
            "*** TRANSPILER ERROR ***",
            ConsoleColor.Yellow);
        WriteColor(
            "=======================================================",
            ConsoleColor.Yellow);
        WriteColor(
            !string.IsNullOrEmpty(toolName)
                ? $"This is likely an issue with the transpiler '{toolName}', not with Effortless."
                : "This is likely an issue with the transpiler, not with Effortless.",
            ConsoleColor.Yellow);
        WriteColor(
            "The transpiler may have received invalid input or encountered an internal error.",
            ConsoleColor.Yellow);
        WriteColor(
            "=======================================================\n",
            ConsoleColor.Yellow);
        PrintExceptionChain(exception);
    }

    private static void PrintExceptionChain(Exception exception)
    {
        for (var current = exception;
             current is not null;
             current = current.InnerException)
        {
            WriteError("ERROR: " + current.Message);
            if (!string.IsNullOrEmpty(current.StackTrace))
            {
                WriteError(current.StackTrace);
            }
        }
    }

    private static void WriteLabel(string label)
    {
        var color = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Blue;
        Console.Write("cli:> ");
        Console.ForegroundColor = color;
        Console.WriteLine(label);
    }

    private static void WriteError(string message) =>
        WriteColor(message, ConsoleColor.Red);

    private static void WriteColor(
        string message,
        ConsoleColor color)
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine(message);
        Console.ForegroundColor = previous;
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        public FixedTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() =>
            _utcNow;
    }
}
