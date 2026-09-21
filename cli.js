#!/usr/bin/env node

'use strict';

const { spawn, execSync } = require('child_process');
const path = require('path');
const fs = require('fs');

const appDir = path.dirname(require.main.filename);
const pkg = require(path.join(appDir, 'package.json'));
const pkgVersion = pkg.version;

// platform/arch -> platform package name. Must match the RID matrix in
// docs/plans/self-contained-binaries.md and scripts/build-platform-packages.mjs.
const PLATFORM_PACKAGES = {
    'darwin:arm64': '@effortlessapi/cli-darwin-arm64',
    'darwin:x64': '@effortlessapi/cli-darwin-x64',
    'win32:x64': '@effortlessapi/cli-win32-x64',
    'win32:arm64': '@effortlessapi/cli-win32-arm64',
    'linux:x64': '@effortlessapi/cli-linux-x64',
    'linux:arm64': '@effortlessapi/cli-linux-arm64',
};

const binaryName = process.platform === 'win32' ? 'Effortless.Cli.exe' : 'Effortless.Cli';

function fail(message) {
    console.error(message);
    process.exit(1);
}

// A git checkout still has src/ next to cli.js; a published install does not.
function isDevCheckout() {
    return fs.existsSync(path.join(appDir, 'src', 'Effortless.Cli', 'Effortless.Cli.csproj'));
}

// Resolve the prebuilt binary shipped by this platform's optional dependency.
// Returns null when the package isn't installed (unsupported platform, or
// --no-optional), so the caller can fall back to developer mode or report it.
function resolvePlatformBinary() {
    const key = `${process.platform}:${process.arch}`;
    const packageName = PLATFORM_PACKAGES[key];
    if (!packageName) {
        fail(
            `Effortless CLI does not ship a binary for ${process.platform}/${process.arch}.\n` +
            `Supported platforms: ${Object.keys(PLATFORM_PACKAGES).join(', ')}.`
        );
    }

    let packageJsonPath;
    try {
        packageJsonPath = require.resolve(`${packageName}/package.json`, { paths: [appDir] });
    } catch {
        return { packageName, missing: true };
    }

    const platformVersion = require(packageJsonPath).version;
    if (platformVersion !== pkgVersion) {
        fail(
            `Effortless CLI version mismatch.\n` +
            `  ${pkg.name}: ${pkgVersion}\n` +
            `  ${packageName}: ${platformVersion}\n` +
            `Reinstall to resync: npm install -g ${pkg.name}@${pkgVersion}`
        );
    }

    const binary = path.join(path.dirname(packageJsonPath), 'bin', binaryName);
    if (!fs.existsSync(binary)) {
        fail(
            `Effortless CLI binary is missing from ${packageName}.\n` +
            `Expected: ${binary}\n` +
            `Reinstall to repair: npm install -g ${pkg.name}@${pkgVersion}`
        );
    }

    // npm has historically dropped the executable bit on extraction; repair it
    // once rather than failing on an otherwise-good install.
    try {
        fs.accessSync(binary, fs.constants.X_OK);
    } catch {
        try {
            fs.chmodSync(binary, 0o755);
            fs.accessSync(binary, fs.constants.X_OK);
        } catch {
            fail(
                `Effortless CLI binary is not executable: ${binary}\n` +
                `Fix with: chmod +x "${binary}"`
            );
        }
    }

    return { packageName, binary };
}

// --- developer mode -------------------------------------------------------
// Only reachable from a git checkout. Published installs never build anything.

// Sync version from package.json into .csproj <Version> and CLI_VERSION constant.
// Mirrors installers/windows/Scripts/build.ps1 so dev builds and installers match.
function syncVersionFromPackageJson() {
    // npm-safe UTC stamp: "2026.424.1854" -> "2026.4.24.1854"
    const m = pkgVersion.match(/^(\d{4})\.(\d{3,4})\.(\d{1,4})$/);
    if (!m) {
        throw new Error(
            `Invalid package version '${pkgVersion}'; expected YYYY.MDD.HHMM without zero-padded numeric components.`
        );
    }
    const monthDay = Number(m[2]);
    const hourMinute = Number(m[3]);
    const month = Math.floor(monthDay / 100);
    const day = monthDay % 100;
    const hour = Math.floor(hourMinute / 100);
    const minute = hourMinute % 100;
    if (month < 1 || month > 12 || day < 1 || day > 31 || hour > 23 || minute > 59) {
        throw new Error(`Invalid UTC date/time in package version '${pkgVersion}'.`);
    }
    const csprojVersion = `${Number(m[1])}.${month}.${day}.${hourMinute}`;
    // Zero-padded, hyphenated, human-unambiguous form of the same instant:
    // "v{yyyy}-{MM}-{dd}-{HHmm}" (24h UTC).
    const pad = (n, width) => String(n).padStart(width, '0');
    const displayVersion = `v${m[1]}-${pad(month, 2)}-${pad(day, 2)}-${pad(hourMinute, 4)}`;

    const updates = [
        {
            file: path.join(appDir, 'src', 'Effortless.Cli', 'Effortless.Cli.csproj'),
            pattern: /<Version>.*?<\/Version>/,
            replacement: `<Version>${csprojVersion}</Version>`,
        },
        {
            file: path.join(appDir, 'src', 'Effortless.Cli.Core', 'CliVersion.cs'),
            pattern: /public const string Value = ".*?";/,
            replacement: `public const string Value = "${pkgVersion}";`,
        },
        {
            file: path.join(appDir, 'src', 'Effortless.Cli.Core', 'CliVersion.cs'),
            pattern: /public const string DisplayVersion = ".*?";/,
            replacement: `public const string DisplayVersion = "${displayVersion}";`,
        },
    ];
    for (const u of updates) {
        if (!fs.existsSync(u.file)) continue;
        const before = fs.readFileSync(u.file, 'utf8');
        const after = before.replace(u.pattern, u.replacement);
        if (after !== before) fs.writeFileSync(u.file, after);
    }
}

// Build from source and return the DLL to run under `dotnet`.
// Rebuilds whenever the compiled DLL wasn't built for the current
// package.json version: a fresh pull of a released commit already has the
// .csproj/CliVersion.cs pre-stamped, so "did I edit a file" is not a
// sufficient signal and a stale DLL would otherwise run forever.
function buildDevCheckout() {
    const projectPath = path.join(appDir, 'src', 'Effortless.Cli', 'Effortless.Cli.csproj');
    const outputPath = path.join(appDir, 'src', 'Effortless.Cli', 'bin', 'Release', 'net8.0', 'Effortless.Cli.dll');
    const buildStampPath = path.join(appDir, 'src', 'Effortless.Cli', 'bin', 'Release', 'net8.0', '.built-version');

    syncVersionFromPackageJson();

    const builtVersion = fs.existsSync(buildStampPath)
        ? fs.readFileSync(buildStampPath, 'utf8').trim()
        : null;

    if (builtVersion !== pkgVersion || !fs.existsSync(outputPath)) {
        console.error('Effortless CLI: developer mode (building from source in this checkout)...');
        try {
            execSync(`dotnet build "${projectPath}" --configuration Release`, {
                stdio: 'inherit',
                cwd: appDir,
            });
            fs.mkdirSync(path.dirname(buildStampPath), { recursive: true });
            fs.writeFileSync(buildStampPath, pkgVersion);
        } catch (error) {
            fail(
                'Failed to build the Effortless CLI from source.\n' +
                'Developer mode needs the .NET 8 SDK: https://dotnet.microsoft.com/download\n' +
                `${error.message}`
            );
        }
    }

    return outputPath;
}

// --- dispatch -------------------------------------------------------------

let command;
let args;

const resolved = isDevCheckout() ? null : resolvePlatformBinary();

if (resolved && !resolved.missing) {
    command = resolved.binary;
    args = process.argv.slice(2);
} else if (isDevCheckout()) {
    command = 'dotnet';
    args = [buildDevCheckout(), ...process.argv.slice(2)];
} else {
    fail(
        `Effortless CLI could not find its binary for ${process.platform}/${process.arch}.\n` +
        `Expected package: ${resolved.packageName}@${pkgVersion}\n` +
        `This usually means the install skipped optional dependencies ` +
        `(--no-optional / --omit=optional).\n` +
        `Reinstall with them enabled: npm install -g ${pkg.name}@${pkgVersion}`
    );
}

try {
    const child = spawn(command, args, { stdio: 'inherit' });
    child.on('error', (error) => {
        console.error('Failed to run CLI:', error);
        process.exit(1);
    });
    child.on('exit', (code, signal) => {
        process.exit(signal ? 1 : (code ?? 1));
    });
} catch (error) {
    console.error('Failed to run CLI:', error);
    process.exit(1);
}
