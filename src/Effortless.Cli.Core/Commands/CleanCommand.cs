using Effortless.Cli.FileSets;
using Effortless.Cli.Options;
using Effortless.Cli.Project;

namespace Effortless.Cli.Commands;

public sealed class CleanCommand
{
    public int Run(CliInvocation invocation)
    {
        var options = invocation.Options;
        var zfsInput = invocation.InputFileSet?.FileSetFiles
            .FirstOrDefault(
                file => file.RelativePath.EndsWith(
                    ".zfs",
                    StringComparison.OrdinalIgnoreCase));
        if (options.clean && zfsInput is not null)
        {
            var ledger = new FileInfo(zfsInput.RelativePath);
            if (ledger.Exists)
            {
                File.ReadAllBytes(ledger.FullName)
                    .CleanZippedFileSet(options.debug, deleteUnchangedNever: true);
                if (!options.preserveZFS)
                {
                    ledger.Delete();
                }
            }

            return 0;
        }

        var runner = new CleanRunner(invocation.Project);
        if (options.cleanAll || options.cleanWithSubprojects)
        {
            runner.CleanAll(
                options.preserveZFS,
                options.purge,
                options.debug,
                options.cleanWithSubprojects);
        }
        else
        {
            runner.Clean(
                invocation.CurrentDirectory,
                options.preserveZFS,
                options.purge,
                options.debug,
                cleanLocal: options.cleanLocal);
        }

        return 0;
    }
}
