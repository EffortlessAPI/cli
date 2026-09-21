#!/usr/bin/env node
// Builds the six self-contained platform packages that @effortlessapi/cli
// depends on, so a published install never needs a .NET SDK.
//
//   node scripts/build-platform-packages.mjs [--rid <rid>] [--skip-publish-dirs]
//
// Each package is generated, never hand-written: all seven package.json files
// (main + six platforms) must carry the exact same version, and release.sh
// relies on that. See docs/plans/self-contained-binaries.md.

import { execFileSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const outRoot = path.join(repoRoot, 'artifacts', 'platform-packages');
const projectPath = path.join(repoRoot, 'src', 'Effortless.Cli', 'Effortless.Cli.csproj');

// rid -> npm package name, os, cpu. `os`/`cpu` are what make npm install
// exactly one of these on any given machine.
export const TARGETS = [
    { rid: 'osx-arm64', pkg: '@effortlessapi/cli-darwin-arm64', os: 'darwin', cpu: 'arm64' },
    { rid: 'osx-x64', pkg: '@effortlessapi/cli-darwin-x64', os: 'darwin', cpu: 'x64' },
    { rid: 'win-x64', pkg: '@effortlessapi/cli-win32-x64', os: 'win32', cpu: 'x64' },
    { rid: 'win-arm64', pkg: '@effortlessapi/cli-win32-arm64', os: 'win32', cpu: 'arm64' },
    { rid: 'linux-x64', pkg: '@effortlessapi/cli-linux-x64', os: 'linux', cpu: 'x64' },
    { rid: 'linux-arm64', pkg: '@effortlessapi/cli-linux-arm64', os: 'linux', cpu: 'arm64' },
];

const mainPackage = JSON.parse(fs.readFileSync(path.join(repoRoot, 'package.json'), 'utf8'));
const version = mainPackage.version;

const ridArgIndex = process.argv.indexOf('--rid');
const onlyRid = ridArgIndex === -1 ? null : process.argv[ridArgIndex + 1];
const targets = onlyRid ? TARGETS.filter((t) => t.rid === onlyRid) : TARGETS;
if (onlyRid && targets.length === 0) {
    console.error(`Unknown RID '${onlyRid}'. Known: ${TARGETS.map((t) => t.rid).join(', ')}`);
    process.exit(2);
}

function buildTarget(target) {
    const stageDir = path.join(outRoot, target.pkg.replace('@effortlessapi/', ''));
    const binDir = path.join(stageDir, 'bin');
    const binaryName = target.os === 'win32' ? 'Effortless.Cli.exe' : 'Effortless.Cli';

    fs.rmSync(stageDir, { recursive: true, force: true });
    fs.mkdirSync(binDir, { recursive: true });

    const publishDir = path.join(repoRoot, 'artifacts', 'publish', target.rid);
    fs.rmSync(publishDir, { recursive: true, force: true });

    console.log(`[${target.rid}] dotnet publish...`);
    execFileSync(
        'dotnet',
        [
            'publish', projectPath,
            '-c', 'Release',
            '-r', target.rid,
            '--self-contained', 'true',
            '-o', publishDir,
        ],
        { stdio: 'inherit', cwd: repoRoot }
    );

    const builtBinary = path.join(publishDir, binaryName);
    if (!fs.existsSync(builtBinary)) {
        throw new Error(`[${target.rid}] expected binary not produced: ${builtBinary}`);
    }
    fs.copyFileSync(builtBinary, path.join(binDir, binaryName));
    fs.chmodSync(path.join(binDir, binaryName), 0o755);

    fs.writeFileSync(
        path.join(stageDir, 'package.json'),
        JSON.stringify(
            {
                name: target.pkg,
                version,
                description: `Prebuilt Effortless CLI binary for ${target.os}/${target.cpu}.`,
                license: mainPackage.license,
                repository: mainPackage.repository,
                os: [target.os],
                cpu: [target.cpu],
                files: ['bin/'],
            },
            null,
            2
        ) + '\n'
    );

    const sizeMb = (fs.statSync(path.join(binDir, binaryName)).size / 1024 / 1024).toFixed(1);
    console.log(`[${target.rid}] ${target.pkg}@${version} staged (${sizeMb} MB)`);
    return stageDir;
}

const staged = targets.map(buildTarget);

console.log(`\nStaged ${staged.length} platform package(s) at ${version}:`);
for (const dir of staged) console.log(`  ${dir}`);
console.log('\nPublish order matters: all platform packages first, main package last.');
