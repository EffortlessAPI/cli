using Effortless.Cli.Config;
using Effortless.Cli.Options;
using Effortless.Cli.Seeds;

namespace Effortless.Cli.Commands;

public sealed class SeedCommands
{
    private readonly SeedCatalogClient _catalog;
    private readonly SeedRepositoryManager _repositories;
    private readonly Func<SeedSources> _sources;
    private readonly SeedLicenseClient _licenses;
    private readonly JwtStore _jwtStore;

    public SeedCommands(
        SeedCatalogClient catalog = null,
        SeedRepositoryManager repositories = null,
        Func<SeedSources> sources = null,
        SeedLicenseClient licenses = null,
        JwtStore jwtStore = null)
    {
        _catalog = catalog ?? new SeedCatalogClient();
        _repositories =
            repositories ?? new SeedRepositoryManager();
        _sources = sources ?? (() => new SeedSources());
        _licenses = licenses ?? new SeedLicenseClient();
        _jwtStore = jwtStore ?? new JwtStore();
    }

    public int ListSources()
    {
        PrintSources(_sources().Load());
        return 0;
    }

    public int AddSource(string account)
    {
        var sources = _sources();
        try
        {
            Console.WriteLine(
                sources.Add(account)
                    ? $"Added seed source '{account}'."
                    : $"Seed source '{account}' is already listed.");
        }
        catch (ArgumentException exception)
        {
            WriteError(exception.Message);
            return -1;
        }

        PrintSources(sources.Load());
        return 0;
    }

    public int RemoveSource(string account)
    {
        var sources = _sources();
        try
        {
            if (!sources.Remove(account))
            {
                WriteError(
                    $"ERROR: Seed source '{account}' is not listed. Current sources: {string.Join(", ", sources.LoadStored())}");
                return -1;
            }
        }
        catch (ArgumentException exception)
        {
            WriteError(exception.Message);
            return -1;
        }

        Console.WriteLine($"Removed seed source '{account}'.");
        PrintSources(sources.Load());
        return 0;
    }

    public int List(CliInvocation invocation)
    {
        var requestedAccount = invocation.RemainingArguments.FirstOrDefault();
        var accounts = string.IsNullOrWhiteSpace(requestedAccount)
            ? _sources().Load().Select(source => source.Account).ToList()
            : [requestedAccount];
        foreach (var account in accounts)
        {
            var seeds = _catalog.ListAsync(account)
                .GetAwaiter()
                .GetResult();
            Console.WriteLine(
                $"Effortless seeds from GitHub account '{account}' ({seeds.Count}):");
            if (seeds.Count == 0)
            {
                Console.WriteLine("  (none)");
            }

            foreach (var seed in seeds)
            {
                Console.WriteLine(
                    $"  {seed.Name}  {seed.Description}".TrimEnd());
            }
        }

        return 0;
    }

    public int Clone(CliInvocation invocation)
    {
        var requested = invocation.RemainingArguments.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(requested))
        {
            throw new ArgumentException(
                "Specify a seed as account/repo, a repository name, or an HTTP(S) clone URL.");
        }

        var destination =
            invocation.RemainingArguments.Skip(1).FirstOrDefault();
        string cloneUrl;
        string defaultDirectory;
        string label;
        if (Uri.TryCreate(
                requested,
                UriKind.Absolute,
                out var uri)
            && uri.Scheme is "https" or "http")
        {
            cloneUrl = uri.ToString();
            defaultDirectory = Path.GetFileNameWithoutExtension(
                uri.AbsolutePath.TrimEnd('/'));
            label = cloneUrl;
        }
        else
        {
            var slash = requested.IndexOf('/');
            SeedRepository seed;
            if (slash > 0)
            {
                seed = FindInAccount(
                    requested[..slash],
                    requested[(slash + 1)..],
                    requested);
            }
            else
            {
                seed = FindAcrossSources(requested);
                if (seed is null)
                {
                    return -1;
                }

                Console.WriteLine(
                    $"Found '{requested}' in seed source '{seed.Account}'.");
            }

            cloneUrl = seed.CloneUrl;
            defaultDirectory = seed.ShortName;
            label = $"{seed.Account}/{seed.Name}";
        }

        destination = string.IsNullOrWhiteSpace(destination)
            ? defaultDirectory
            : destination;
        var clonedPath = _repositories.Clone(
            cloneUrl,
            destination);

        var licenseKey = invocation.Options.licenseKey;
        if (!string.IsNullOrWhiteSpace(licenseKey)
            && !TryBindLicense(licenseKey, requested, cloneUrl, destination, clonedPath))
        {
            return -1;
        }

        Console.WriteLine(
            $"Cloned Effortless seed {label} to {clonedPath}");
        Console.WriteLine(
            $"Run `cd \"{clonedPath}\" && effortless build` when you are ready to execute its pipeline.");
        return 0;
    }

    /// <summary>
    /// Spends the license key on the just-cloned repository. Requires a
    /// signed-in session -- a key is held by an account, so an anonymous
    /// clone cannot claim one. On any failure the freshly cloned directory
    /// is removed rather than left as a half-bound repository the buyer
    /// never actually holds a license for.
    /// </summary>
    private bool TryBindLicense(
        string licenseKey,
        string seedId,
        string repoUrl,
        string projectName,
        string clonedPath)
    {
        if (!_jwtStore.IsAuthenticated())
        {
            WriteError(
                "-licenseKey requires a signed-in session (a license key is held by an account). Run `effortless login` first, then retry the clone.");
            TryRemoveDirectory(clonedPath);
            return false;
        }

        var jwt = _jwtStore.GetStoredJWTToken();
        var bound = _licenses.Bind(
            jwt, licenseKey, seedId, repoUrl, projectName, sourceRulebook: null, out var error);
        if (bound is null)
        {
            WriteError($"Could not bind license key: {error}");
            TryRemoveDirectory(clonedPath);
            return false;
        }

        var envPath = Path.Combine(clonedPath, "effortless.env");
        EnvFile.WriteEnvValue(envPath, "EFFORTLESS_SEED_LICENSE_KEY", licenseKey);
        Console.WriteLine($"Bound license key to this repository. Wrote EFFORTLESS_SEED_LICENSE_KEY to {envPath}.");
        return true;
    }

    private static void TryRemoveDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception)
        {
            WriteError($"Cloned repository at {path} could not be removed automatically: {exception.Message}");
        }
    }

    private SeedRepository FindInAccount(
        string account,
        string name,
        string requested)
    {
        var matches = Matches(account, name);
        if (matches.Count != 1)
        {
            throw new InvalidOperationException(
                matches.Count == 0
                    ? $"Seed '{requested}' was not found."
                    : $"Seed '{requested}' is ambiguous: {string.Join(", ", matches.Select(seed => $"{seed.Account}/{seed.Name}"))}. Use account/repo.");
        }

        return matches[0];
    }

    private SeedRepository FindAcrossSources(string name)
    {
        var sources = _sources().Load()
            .Select(source => source.Account)
            .ToList();
        var matches = sources
            .SelectMany(account => Matches(account, name))
            .ToList();
        if (matches.Count == 1)
        {
            return matches[0];
        }

        WriteError(
            matches.Count == 0
                ? $"Seed '{name}' was not found in any seed source ({string.Join(", ", sources)})."
                : $"Seed '{name}' is ambiguous: {string.Join(", ", matches.Select(seed => $"{seed.Account}/{seed.Name}"))}. Use account/repo.");
        return null;
    }

    private List<SeedRepository> Matches(string account, string name) =>
        _catalog.ListAsync(account)
            .GetAwaiter()
            .GetResult()
            .Where(seed =>
                string.Equals(
                    seed.Name,
                    name,
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    seed.ShortName,
                    name,
                    StringComparison.OrdinalIgnoreCase))
            .ToList();

    private static void PrintSources(IReadOnlyList<SeedSource> sources)
    {
        Console.WriteLine("Seed sources (searched in order):");
        foreach (var source in sources)
        {
            Console.WriteLine($"  {source.Account}{source.Marker}");
        }
    }

    private static void WriteError(string message)
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine(message);
        Console.ForegroundColor = previous;
    }
}
