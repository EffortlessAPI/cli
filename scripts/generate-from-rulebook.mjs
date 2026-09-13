#!/usr/bin/env node

import { readFile, writeFile } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import path from "node:path";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const rulebookPath = path.join(
  root,
  "effortless-rulebook",
  "effortless-rulebook.json",
);
const README_START = "<!-- cli-commands:start -->";
const README_END = "<!-- cli-commands:end -->";
const checkOnly = process.argv.slice(2).includes("--check");
const unknownArguments = process.argv
  .slice(2)
  .filter((argument) => argument !== "--check");

if (unknownArguments.length > 0) {
  console.error(`Unknown argument(s): ${unknownArguments.join(", ")}`);
  process.exitCode = 2;
} else {
  await main();
}

async function main() {
  const rulebook = JSON.parse(await readFile(rulebookPath, "utf8"));
  const dispositions = new Map(
    rulebook.Dispositions.data.map((row) => [row.DispositionId, row]),
  );
  const isRetained = (row) =>
    dispositions.get(row.Disposition)?.IsRetained === true;
  const retainedOptions = rulebook.CliOptions.data.filter(isRetained);

  const outputs = new Map([
    [
      "src/Effortless.Cli.Core/Options/CliOptions.g.cs",
      generateCliOptions(rulebook, retainedOptions),
    ],
    [
      "src/Effortless.Cli.Core/Options/BarewordVerbs.g.cs",
      generateBarewordVerbs(retainedOptions),
    ],
    [
      "docs/cli-reference.md",
      generateCliReference(rulebook, dispositions, retainedOptions),
    ],
    [
      "tests/Effortless.Cli.E2E/TestManifest.g.json",
      generateTestManifest(rulebook, new Set(["black-box"])),
    ],
    [
      "tests/Effortless.Cli.Tests/TestManifest.g.json",
      generateTestManifest(rulebook, new Set(["unit", "contract"])),
    ],
    [
      "src/Effortless.Cli.Core/Options/CliOptionMetadata.g.cs",
      generateCliOptionMetadata(rulebook, retainedOptions),
    ],
  ]);

  // README carries a generated block between markers rather than being
  // generated whole, so it is handled apart from the outputs map.
  const readmePath = path.join(root, "README.md");
  const readmeActual = await readFile(readmePath, "utf8");
  const readmeExpected = renderReadmeCommands(
    readmeActual,
    rulebook,
    retainedOptions,
  );

  let drifted = false;
  for (const [relativePath, expected] of outputs) {
    const absolutePath = path.join(root, relativePath);
    if (!checkOnly) {
      await writeFile(absolutePath, expected, "utf8");
      console.log(`generated ${relativePath}`);
      continue;
    }

    let actual;
    try {
      actual = await readFile(absolutePath, "utf8");
    } catch (error) {
      if (error.code !== "ENOENT") {
        throw error;
      }
      actual = "";
    }

    if (actual !== expected) {
      drifted = true;
      process.stderr.write(unifiedDiff(relativePath, actual, expected));
    }
  }

  if (!checkOnly) {
    if (readmeActual !== readmeExpected) {
      await writeFile(readmePath, readmeExpected, "utf8");
    }
    console.log("generated README.md (cli-commands block)");
  } else if (readmeActual !== readmeExpected) {
    drifted = true;
    process.stderr.write(
      unifiedDiff("README.md", readmeActual, readmeExpected),
    );
  }

  if (drifted) {
    process.exitCode = 1;
  }
}

/**
 * Replaces only the text between the cli-commands markers so the README's
 * command summary and -help are generated from the same rulebook rows.
 */
function renderReadmeCommands(readme, rulebook, retainedOptions) {
  const start = readme.indexOf(README_START);
  const end = readme.indexOf(README_END);
  if (start < 0 || end < 0 || end < start) {
    throw new Error(
      `README.md is missing the ${README_START} / ${README_END} markers.`,
    );
  }

  const categories = sortedCategories(rulebook);
  const lines = ["", ""];
  for (const category of categories) {
    const primary = retainedOptions.filter(
      (option) =>
        option.Category === category.OptionCategoryId &&
        option.Tier === "primary",
    );
    if (primary.length === 0) {
      continue;
    }

    lines.push(`### ${category.Label}`, "");
    for (const option of primary) {
      lines.push(`- \`${option.Flag}\` — ${option.HelpSummary ?? ""}`);
    }

    lines.push("");
  }

  lines.push(
    "Run `effortless -help <category|option>` for one topic, or",
    "`effortless -help all` for every option.",
    "",
  );

  return (
    readme.slice(0, start + README_START.length) +
    lines.join("\n") +
    readme.slice(end)
  );
}

function sortedCategories(rulebook) {
  return [...rulebook.OptionCategories.data].sort(
    (left, right) =>
      Number(left.SortOrder) - Number(right.SortOrder) ||
      left.OptionCategoryId.localeCompare(right.OptionCategoryId),
  );
}

/**
 * Emits the tier/category/parent/help metadata the dispatcher's -help topics
 * need at runtime. The Plossum attributes in CliOptions.g.cs carry only the
 * one-line Description, which is not enough to group or filter.
 */
function generateCliOptionMetadata(rulebook, retainedOptions) {
  const categories = sortedCategories(rulebook);
  const lines = [
    "// <auto-generated> from effortless-rulebook.json — do not edit",
    "using System.Collections.ObjectModel;",
    "",
    "namespace Effortless.Cli.Options;",
    "",
    "public sealed record CliOptionInfo(",
    "    string Id,",
    "    string Flag,",
    "    string Category,",
    "    string Tier,",
    "    string ParentOption,",
    "    string VerbFamily,",
    "    string Scope,",
    "    string Aliases,",
    "    string BarewordForms,",
    "    string HelpSummary,",
    "    string HelpDetail,",
    "    string Example);",
    "",
    "public sealed record CliCategoryInfo(",
    "    string Id,",
    "    string Label,",
    "    string Description);",
    "",
    "public static class CliOptionMetadata",
    "{",
    "    public static IReadOnlyList<CliCategoryInfo> Categories { get; } =",
    "        new ReadOnlyCollection<CliCategoryInfo>(",
    "            new List<CliCategoryInfo>",
    "            {",
  ];

  for (const category of categories) {
    lines.push(
      "                new(",
      `                    "${escapeCSharpString(category.OptionCategoryId)}",`,
      `                    "${escapeCSharpString(category.Label)}",`,
      `                    "${escapeCSharpString(category.Description)}"),`,
    );
  }

  lines.push(
    "            });",
    "",
    "    public static IReadOnlyList<CliOptionInfo> Options { get; } =",
    "        new ReadOnlyCollection<CliOptionInfo>(",
    "            new List<CliOptionInfo>",
    "            {",
  );

  for (const option of retainedOptions) {
    lines.push(
      "                new(",
      `                    "${escapeCSharpString(option.CliOptionId)}",`,
      `                    "${escapeCSharpString(option.Flag)}",`,
      `                    "${escapeCSharpString(option.Category)}",`,
      `                    "${escapeCSharpString(option.Tier)}",`,
      `                    "${escapeCSharpString(option.ParentOption)}",`,
      `                    "${escapeCSharpString(option.VerbFamily)}",`,
      `                    "${escapeCSharpString(option.Scope)}",`,
      `                    "${escapeCSharpString(option.Aliases)}",`,
      `                    "${escapeCSharpString(option.BarewordForms)}",`,
      `                    "${escapeCSharpString(option.HelpSummary)}",`,
      `                    "${escapeCSharpString(option.HelpDetail)}",`,
      `                    "${escapeCSharpString(option.Example)}"),`,
    );
  }

  lines.push("            });", "}", "");
  return lines.join("\n");
}

function generateCliOptions(rulebook, options) {
  const facts = new Map(
    rulebook.ProjectFacts.data.map((row) => [row.ProjectFactId, row.Value]),
  );
  const typeNames = new Map([
    ["bool", "bool"],
    ["string", "string"],
    ["int", "int"],
    ["list", "List<string>"],
  ]);
  const listOptions = options.filter((option) => option.ValueType === "list");
  const defaultWaitTimeout = facts.get("default-wait-timeout-ms");

  const lines = [
    "// <auto-generated> from effortless-rulebook.json — do not edit",
    "using Plossum.CommandLine;",
    "",
    "namespace Effortless.Cli.Options;",
    "",
    "[CommandLineManager(",
    `    ApplicationName = "${escapeCSharpString(facts.get("help-application-name"))}",`,
    `    Copyright = "${escapeCSharpString(facts.get("help-copyright"))}",`,
    `    Description = @"${escapeCSharpVerbatimString(facts.get("help-manager-description"))}",`,
    "    EnabledOptionStyles =",
    "        OptionStyles.Windows |",
    "        OptionStyles.Unix |",
    "        OptionStyles.File)]",
    "public class CliOptions",
    "{",
    "    public CliOptions()",
    "    {",
    ...listOptions.map(
      (option) =>
        `        ${option.CliOptionId} = new List<string>();`,
    ),
  ];

  if (defaultWaitTimeout !== undefined) {
    lines.push(`        waitTimeout = ${Number(defaultWaitTimeout)};`);
  }

  lines.push("    }");

  for (const option of options) {
    const typeName = typeNames.get(option.ValueType);
    if (!typeName) {
      throw new Error(
        `Unsupported CliOptions.ValueType '${option.ValueType}' for ${option.CliOptionId}`,
      );
    }

    lines.push(
      "",
      `    [CommandLineOption(Description = "${escapeCSharpString(option.HelpText ?? "")}", MinOccurs = 0, Aliases = "${escapeCSharpString(option.Aliases ?? "")}")]`,
      `    public ${typeName} ${option.CliOptionId} { get; set; }`,
    );
  }

  lines.push("}", "");
  return lines.join("\n");
}

function generateBarewordVerbs(options) {
  const entries = [];
  const stringSetters = [];
  // Bareword forms of these consume the tool position as their value, rather
  // than the tool position being a tool name. D16 renamed the *Url ids to
  // *ToolUrl; D17 added pin, whose bareword takes "<tool> <version|url>";
  // step 14 added the seed-source verbs, whose bareword takes "<account>".
  const consumedStringOptions = new Set([
    "viewToolUrl",
    "setToolUrl",
    "removeToolUrl",
    "searchTools",
    "addSeedSource",
    "removeSeedSource",
  ]);

  // Options whose bareword form is normalized by CliArgumentParser's
  // NoDashCommandForms table instead (it consumes a trailing value argument
  // while leaving the tool position intact). They must not also appear as a
  // bareword verb here, or the verb table would try to assign bool to string.
  // The save watchers are there too because their value is optional: the
  // parser supplies the default rulebook when no file is named.
  const parserHandledStringOptions = new Set([
    "pin",
    "compileOnSave",
    "buildOnSave",
  ]);

  for (const option of options) {
    for (const verb of commaSeparated(option.BarewordForms)) {
      if (parserHandledStringOptions.has(option.CliOptionId)) {
        continue;
      }

      const isConsumedStringOption =
        option.ValueType === "string" &&
        option.ConsumesNextArgAsTool === true &&
        consumedStringOptions.has(option.CliOptionId);
      entries.push(
        isConsumedStringOption
          ? `                ["${escapeCSharpString(verb)}"] = _ => { },`
          : `                ["${escapeCSharpString(verb)}"] = options => options.${option.CliOptionId} = true,`,
      );

      if (isConsumedStringOption) {
        stringSetters.push(
          `                ["${escapeCSharpString(verb)}"] = (options, value) => options.${option.CliOptionId} = ${stringSetterValue(option.CliOptionId)},`,
        );
      }
    }
  }

  return [
    "using System.Collections.ObjectModel;",
    "",
    "namespace Effortless.Cli.Options;",
    "",
    "public static class BarewordVerbs",
    "{",
    "    public static IReadOnlyDictionary<string, Action<CliOptions>> Verbs { get; } =",
    "        new ReadOnlyDictionary<string, Action<CliOptions>>(",
    "            new Dictionary<string, Action<CliOptions>>(StringComparer.Ordinal)",
    "            {",
    ...entries,
    "            });",
    "",
    "    private static IReadOnlyDictionary<string, Action<CliOptions, string>> StringOptionSetters { get; } =",
    "        new ReadOnlyDictionary<string, Action<CliOptions, string>>(",
    "            new Dictionary<string, Action<CliOptions, string>>(StringComparer.Ordinal)",
    "            {",
    ...stringSetters,
    "            });",
    "",
    "    public static bool TryApply(",
    "        CliOptions options,",
    "        IList<string> remainingArguments,",
    "        out string transpiler)",
    "    {",
    "        transpiler = null;",
    "        if (options is null || remainingArguments is null || remainingArguments.Count == 0)",
    "        {",
    "            return false;",
    "        }",
    "",
    '        var verb = $"{remainingArguments[0]}".ToLower();',
    "        if (!Verbs.TryGetValue(verb, out var apply))",
    "        {",
    "            return false;",
    "        }",
    "",
    "        apply(options);",
    "        transpiler = remainingArguments.Count > 1",
    "            ? remainingArguments[1]",
    "            : null;",
    "",
    "        if (StringOptionSetters.TryGetValue(verb, out var setStringOption))",
    "        {",
    "            setStringOption(options, transpiler);",
    "        }",
    "",
    "        remainingArguments.RemoveAt(0);",
    "        return true;",
    "    }",
    "}",
    "",
  ].join("\n");
}

function generateCliReference(
  rulebook,
  dispositions,
  retainedOptions,
) {
  const categories = [...rulebook.OptionCategories.data].sort(
    (left, right) =>
      Number(left.SortOrder) - Number(right.SortOrder) ||
      left.OptionCategoryId.localeCompare(right.OptionCategoryId),
  );
  const optionsByCategory = new Map(
    categories.map((category) => [category.OptionCategoryId, []]),
  );
  for (const option of retainedOptions) {
    optionsByCategory.get(option.Category)?.push(option);
  }

  const facts = new Map(
    rulebook.ProjectFacts.data.map((row) => [row.ProjectFactId, row.Value]),
  );
  const lines = [
    "<!-- Generated from effortless-rulebook.json. Do not edit. -->",
    "# Effortless CLI reference",
    "",
    "## Help syntax",
    "",
    "```text",
    facts.get("help-syntax-line") ?? "",
    "```",
    "",
  ];

  for (const category of categories) {
    const categoryOptions = optionsByCategory.get(category.OptionCategoryId);
    if (!categoryOptions || categoryOptions.length === 0) {
      continue;
    }

    lines.push(`## ${category.Label}`, "", category.Description, "");
    for (const option of categoryOptions) {
      lines.push(
        `### \`${option.Flag}\``,
        "",
        `- Aliases: ${formatCodeList(option.Aliases)}`,
        `- Bareword forms: ${formatCodeList(option.BarewordForms)}`,
        `- Value type: \`${option.ValueType === "list" ? "List<string>" : option.ValueType}\``,
        `- Help text: ${option.HelpText ?? ""}`,
        `- Description: ${option.Description ?? ""}`,
        "",
      );
    }
  }

  lines.push("## Exit codes", "");
  for (const exitCode of rulebook.ExitCodes.data) {
    lines.push(
      `- \`${exitCode.ProcessExitCode}\` — **${exitCode.Meaning}**: ${exitCode.Description}`,
    );
  }

  lines.push("", "## Configuration files", "");
  for (const configFile of rulebook.ConfigFiles.data.filter(
    (row) => dispositions.get(row.Disposition)?.IsRetained === true,
  )) {
    lines.push(
      `- \`${configFile.Path}\` — ${configFile.Purpose} ${configFile.Description}`.trimEnd(),
    );
  }

  lines.push("", "## Environment variables and keys", "");
  for (const variable of rulebook.EnvVariables.data.filter(
    (row) => dispositions.get(row.Disposition)?.IsRetained === true,
  )) {
    lines.push(
      `- \`${variable.EnvVariableId}\` (${variable.Source}) — ${variable.Purpose} ${variable.Description}`.trimEnd(),
    );
  }

  lines.push("");
  return lines.join("\n");
}

function generateTestManifest(rulebook, kinds) {
  const suiteKinds = new Map(
    rulebook.TestSuites.data.map((row) => [row.TestSuiteId, row.Kind]),
  );
  const manifest = rulebook.TestCases.data
    // A row marked "deleted" describes behavior that no longer exists in v2;
    // it must not appear in the manifest the coverage gates read.
    .filter((row) => row.Status !== "deleted")
    .filter((row) => kinds.has(suiteKinds.get(row.Suite)))
    .map((row) => ({
      id: row.TestCaseId,
      suite: row.Suite,
      priority: row.Priority,
      status: row.Status,
      slow: row.Slow === true,
      interactive: row.Interactive === true,
    }));

  return `${JSON.stringify(manifest, null, 2)}\n`;
}

function commaSeparated(value) {
  return `${value ?? ""}`
    .split(",")
    .map((item) => item.trim())
    .filter(Boolean);
}

function formatCodeList(value) {
  const items = commaSeparated(value);
  return items.length === 0
    ? "None"
    : items.map((item) => `\`${item}\``).join(", ");
}

function stringSetterValue(optionId) {
  return optionId === "viewToolUrl" ? "value ?? string.Empty" : "value";
}

function escapeCSharpString(value) {
  return `${value ?? ""}`
    .replaceAll("\\", "\\\\")
    .replaceAll('"', '\\"')
    .replaceAll("\r", "\\r")
    .replaceAll("\n", "\\n")
    .replaceAll("\t", "\\t");
}

function escapeCSharpVerbatimString(value) {
  return `${value ?? ""}`.replaceAll('"', '""');
}

function unifiedDiff(relativePath, actual, expected) {
  const actualLines = withoutTrailingEmpty(actual.split("\n"));
  const expectedLines = withoutTrailingEmpty(expected.split("\n"));
  const lines = [
    `--- a/${relativePath}`,
    `+++ b/${relativePath}`,
    `@@ -1,${actualLines.length} +1,${expectedLines.length} @@`,
    ...actualLines.map((line) => `-${line}`),
    ...expectedLines.map((line) => `+${line}`),
    "",
  ];
  return lines.join("\n");
}

function withoutTrailingEmpty(lines) {
  return lines.length > 0 && lines.at(-1) === "" ? lines.slice(0, -1) : lines;
}
