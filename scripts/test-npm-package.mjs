#!/usr/bin/env node

import { execFileSync } from "node:child_process";
import { existsSync, mkdtempSync, readFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const packageJson = JSON.parse(
  readFileSync(path.join(root, "package.json"), "utf8"),
);
const expectedAliases = ["aic", "aicapture", "effortless", "ssotme"];

assertEqual(packageJson.name, "@effortlessapi/cli", "package name");
assertEqual(
  Object.keys(packageJson.bin).sort().join(","),
  expectedAliases.join(","),
  "binary aliases",
);
for (const alias of expectedAliases) {
  assertEqual(packageJson.bin[alias], "cli.js", `${alias} shim`);
}

// `-version` prints the zero-padded display form of the same instant that
// package.json encodes: "2026.921.2345" -> "v2026-09-21-2345". Derive it here
// rather than comparing against the npm form, which never matches.
const expectedDisplayVersion = displayVersionFor(packageJson.version);

const work = mkdtempSync(path.join(tmpdir(), "effortless-npm-package-"));
const packResult = JSON.parse(
  run("npm", ["pack", "--json", "--pack-destination", work], {
    cwd: root,
  }),
);
if (!Array.isArray(packResult) || packResult.length !== 1) {
  throw new Error(`Expected one npm tarball, got: ${JSON.stringify(packResult)}`);
}

const tarball = path.join(work, packResult[0].filename);
const prefix = path.join(work, "prefix");

// The published main package is only a launcher; the CLI itself lives in a
// per-platform optional dependency that is not on the registry yet at release
// time. Install this host's staged platform package alongside the tarball, or
// every alias fails with the (correct) "could not find its binary" error.
const platformPackageName = platformPackageForHost();
const platformPackageDir = path.join(
  root,
  "artifacts",
  "platform-packages",
  platformPackageName.replace("@effortlessapi/", ""),
);
if (!existsSync(path.join(platformPackageDir, "package.json"))) {
  throw new Error(
    `Missing staged platform package for this host: ${platformPackageDir}\n` +
      `Run: node scripts/build-platform-packages.mjs`,
  );
}
const stagedPlatformVersion = JSON.parse(
  readFileSync(path.join(platformPackageDir, "package.json"), "utf8"),
).version;
assertEqual(
  stagedPlatformVersion,
  packageJson.version,
  `${platformPackageName} version`,
);

const platformTarballResult = JSON.parse(
  run("npm", ["pack", "--json", "--pack-destination", work], {
    cwd: platformPackageDir,
  }),
);
const platformTarball = path.join(work, platformTarballResult[0].filename);

run(
  "npm",
  ["install", "--global", "--prefix", prefix, platformTarball, tarball],
  {
    cwd: work,
    stdio: "inherit",
  },
);

for (const alias of expectedAliases) {
  const executable =
    process.platform === "win32"
      ? path.join(prefix, `${alias}.cmd`)
      : path.join(prefix, "bin", alias);
  const output = run(executable, ["-version"], {
    cwd: work,
    maxBuffer: 20 * 1024 * 1024,
  });
  const lastLine = output.trim().split(/\r?\n/).at(-1);
  assertEqual(lastLine, expectedDisplayVersion, `${alias} -version`);
}

console.log(
  `Verified ${packageJson.name}@${packageJson.version} from ${tarball}`,
);
console.log(`Aliases: ${expectedAliases.join(", ")}`);

// Mirrors syncVersionFromPackageJson() in cli.js: "YYYY.MDD.HHMM" is the
// npm-safe form of an instant, "vYYYY-MM-DD-HHMM" is what the CLI prints.
function displayVersionFor(version) {
  const m = version.match(/^(\d{4})\.(\d{3,4})\.(\d{1,4})$/);
  if (!m) {
    throw new Error(`Invalid package version '${version}'.`);
  }
  const monthDay = Number(m[2]);
  const pad = (n, width) => String(n).padStart(width, "0");
  return `v${m[1]}-${pad(Math.floor(monthDay / 100), 2)}-${pad(monthDay % 100, 2)}-${pad(Number(m[3]), 4)}`;
}

// Mirrors PLATFORM_PACKAGES in cli.js. Kept in sync by the version assertion
// above: a mismatch here surfaces as a failed install rather than a bad publish.
function platformPackageForHost() {
  const key = `${process.platform}:${process.arch}`;
  const known = {
    "darwin:arm64": "@effortlessapi/cli-darwin-arm64",
    "darwin:x64": "@effortlessapi/cli-darwin-x64",
    "win32:x64": "@effortlessapi/cli-win32-x64",
    "win32:arm64": "@effortlessapi/cli-win32-arm64",
    "linux:x64": "@effortlessapi/cli-linux-x64",
    "linux:arm64": "@effortlessapi/cli-linux-arm64",
  };
  const name = known[key];
  if (!name) {
    throw new Error(`No platform package is published for ${key}.`);
  }
  return name;
}

function run(command, args, options = {}) {
  if (process.platform !== "win32") {
    return execFileSync(command, args, {
      encoding: "utf8",
      ...options,
    });
  }

  const commandLine = [command, ...args].map(quoteWindows).join(" ");
  return execFileSync(
    process.env.ComSpec ?? "cmd.exe",
    ["/d", "/s", "/c", commandLine],
    {
      encoding: "utf8",
      ...options,
    },
  );
}

function quoteWindows(value) {
  return `"${String(value).replaceAll('"', '""')}"`;
}

function assertEqual(actual, expected, label) {
  if (actual !== expected) {
    throw new Error(`${label}: expected '${expected}', got '${actual}'`);
  }
}
