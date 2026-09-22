#!/bin/bash
set -euo pipefail

EXPECTED_PACKAGE="@effortlessapi/cli"
DRY_RUN=false
SKIP_NPM=false

for argument in "$@"; do
    case "$argument" in
        --dry-run)
            DRY_RUN=true
            ;;
        --skip-npm)
            SKIP_NPM=true
            ;;
        *)
            echo "Usage: scripts/release.sh [--dry-run] [--skip-npm]" >&2
            exit 2
            ;;
    esac
done

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

if [ -n "$(git status --porcelain)" ]; then
    echo "ERROR: release requires a clean working tree." >&2
    exit 1
fi

BRANCH="$(git branch --show-current)"
if [ "$BRANCH" != "main" ]; then
    echo "ERROR: release must run from main; current branch is '${BRANCH:-detached}'." >&2
    exit 1
fi

PACKAGE_NAME="$(node -p "require('./package.json').name")"
if [ "$PACKAGE_NAME" != "$EXPECTED_PACKAGE" ]; then
    echo "ERROR: package.json name must be $EXPECTED_PACKAGE; got $PACKAGE_NAME" >&2
    exit 1
fi

VERSION="$(node -e 'const d = new Date(); console.log(`${d.getUTCFullYear()}.${(d.getUTCMonth() + 1) * 100 + d.getUTCDate()}.${d.getUTCHours() * 100 + d.getUTCMinutes()}`)')"
TAG="v${VERSION}"

if [ "$DRY_RUN" = true ]; then
    echo "Dry run: release ${EXPECTED_PACKAGE}@${VERSION}"
    if [ "$SKIP_NPM" = true ]; then
        echo "Would SKIP npm authentication and npm publish (--skip-npm)."
    else
        echo "Would verify npm authentication."
    fi
    echo "Would stamp package.json, Effortless.Cli.csproj, and CliVersion.cs (Value, DisplayVersion, CommitSha)."
    echo "Would run the full .NET and packaged-alias test suites."
    echo "Would build all six self-contained platform packages at ${VERSION}."
    echo "Would commit and push ${TAG} from main."
    echo "Would create GitHub release ${TAG}."
    if [ "$SKIP_NPM" = true ]; then
        echo "Would NOT publish ${EXPECTED_PACKAGE}@${VERSION} or its platform packages (--skip-npm)."
    else
        echo "Would publish the six platform packages FIRST, then ${EXPECTED_PACKAGE}@${VERSION} LAST."
    fi
    exit 0
fi

if [ "$SKIP_NPM" = true ]; then
    echo "Skipping npm authentication and npm publish (--skip-npm)."
else
    echo "Checking npm authentication..."
    npm whoami >/dev/null
fi

echo "Updating package.json version to ${VERSION}..."
npm pkg set "version=${VERSION}"

# The six platform packages are pinned to an exact version, and cli.js refuses
# to run unless it matches the launcher's exactly. Stamping only the top-level
# version leaves those pins on the PREVIOUS release, so npm silently skips
# every optional dependency (they resolve to a version that does not exist yet)
# and the install lands with no binary at all.
echo "Stamping optionalDependencies to ${VERSION}..."
node - "$VERSION" <<'NODE'
const fs = require('fs');
const version = process.argv[2];
const pkg = JSON.parse(fs.readFileSync('package.json', 'utf8'));
for (const name of Object.keys(pkg.optionalDependencies ?? {})) {
    pkg.optionalDependencies[name] = version;
}
fs.writeFileSync('package.json', JSON.stringify(pkg, null, 2) + '\n');
NODE

COMMIT_SHA="$(git rev-parse HEAD)"
echo "Stamping CommitSha (${COMMIT_SHA})..."
CLI_VERSION_FILE="src/Effortless.Cli.Core/CliVersion.cs"
sed -i.bak -E "s/public const string CommitSha = \".*\";/public const string CommitSha = \"${COMMIT_SHA}\";/" "$CLI_VERSION_FILE"
rm -f "${CLI_VERSION_FILE}.bak"

echo "Synchronizing and building CLI version sources..."
node cli.js -version

echo "Running the full test suite..."
dotnet test Effortless.Cli.sln --configuration Release

echo "Building self-contained platform packages..."
node scripts/build-platform-packages.mjs

PLATFORM_PACKAGE_DIRS=()
while IFS= read -r dir; do
    PLATFORM_PACKAGE_DIRS+=("$dir")
done < <(find artifacts/platform-packages -mindepth 1 -maxdepth 1 -type d | sort)

if [ "${#PLATFORM_PACKAGE_DIRS[@]}" -ne 6 ]; then
    echo "ERROR: expected 6 platform packages, found ${#PLATFORM_PACKAGE_DIRS[@]}." >&2
    exit 1
fi

# All seven package.json versions must match exactly, or cli.js refuses to run
# at all (its version-skew guard). Catch that here rather than after publishing.
for dir in "${PLATFORM_PACKAGE_DIRS[@]}"; do
    platform_version="$(node -p "require('./${dir}/package.json').version")"
    if [ "$platform_version" != "$VERSION" ]; then
        echo "ERROR: ${dir} is ${platform_version}, expected ${VERSION}." >&2
        exit 1
    fi
done

# Every optionalDependency pin must also be exactly VERSION. A stale pin does
# not fail the install — npm skips an optional dependency it cannot resolve —
# so this would ship an install with no binary and no error until first run.
node - "$VERSION" <<'NODE'
const pkg = require('./package.json');
const bad = Object.entries(pkg.optionalDependencies ?? {})
    .filter(([, pinned]) => pinned !== process.argv[2]);
if (bad.length > 0) {
    console.error(
        `ERROR: optionalDependencies are not pinned to ${process.argv[2]}:\n` +
        bad.map(([name, pinned]) => `  ${name}: ${pinned}`).join('\n'));
    process.exit(1);
}
NODE

if [ "$SKIP_NPM" = true ]; then
    echo "Skipping npm package validation (--skip-npm)."
else
    echo "Validating public npm package..."
    npm publish --dry-run --access public
    npm run test:package
fi

echo "Committing and pushing release..."
git add \
    package.json \
    src/Effortless.Cli/Effortless.Cli.csproj \
    src/Effortless.Cli.Core/CliVersion.cs
git commit -m "Release ${TAG}"
git push

echo "Creating GitHub release ${TAG}..."
gh release create "${TAG}" --title "${TAG}" --notes "Release ${TAG}"

if [ "$SKIP_NPM" = true ]; then
    echo ""
    echo "Done! ${TAG} is tagged, pushed, and released on GitHub — npm publish SKIPPED (--skip-npm)."
    echo "Install from source: git clone https://github.com/EffortlessAPI/cli.git && cd cli && npm install -g ."
    echo "npm publish is still pending. Once npm auth is restored, run these IN ORDER"
    echo "(platform packages first; the main package resolves them, so it goes last):"
    for dir in "${PLATFORM_PACKAGE_DIRS[@]}"; do
        echo "    npm publish ${dir} --access public"
    done
    echo "    npm publish --access public"
    echo "from a clean checkout of ${TAG} to finish this release."
else
    # Order is the entire mitigation for a partial publish: the main package
    # must not exist on the registry until every platform package it depends
    # on is already resolvable, or an install lands with no binary.
    echo "Publishing six platform packages first..."
    for dir in "${PLATFORM_PACKAGE_DIRS[@]}"; do
        echo "  publishing ${dir}..."
        npm publish "$dir" --access public
    done

    echo "Publishing ${EXPECTED_PACKAGE}@${VERSION} last..."
    npm publish --access public

    echo "Done! ${EXPECTED_PACKAGE}@${VERSION} is published and MSI/PKG builds will start automatically."
    echo "Install: npm install -g ${EXPECTED_PACKAGE}"
    echo "Track progress: gh run list --workflow=build-windows.yml && gh run list --workflow=build-mac.yml"
fi
