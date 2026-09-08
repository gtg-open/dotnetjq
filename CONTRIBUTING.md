# Contributing to DotNetJq

Thank you for improving DotNetJq. Compatibility work is easiest to review when
the managed change remains visibly connected to jq 1.8.2.

## Compatibility rules

- jq 1.8.2 commit `34f7186b86743a083a589741b6cea95293524108`
  is the semantic source of truth.
- Read `porting/PORTING_MANIFEST.json` before changing the compatibility core.
- Preserve upstream file mappings, function names, and explicit copy/move/free
  ownership boundaries. Prefer small file/function-local changes.
- Every upstream file remains classified `PORT`, `PROXY`, `GENERATED`, `REUSE`,
  or `OMITTED`. Update implementation, mapping, status, tests, and provenance in
  the same pull request.
- Never weaken, skip, or delete a failing compatibility test to make CI green.
- Production code must never launch jq. Test tooling may use the pinned jq
  executable only as an oracle.
- Edit maintained grammars and generators, then regenerate checked-in outputs;
  do not hand-edit generated parser or lexer files.

## Build and test

The exact .NET 10 SDK is pinned in `global.json`.

```sh
dotnet restore DotNetJq.sln
dotnet build DotNetJq.sln --configuration Release --no-restore -t:Rebuild -m:1 -nr:false
dotnet test tests/DotNetJq.Tests/DotNetJq.Tests.csproj \
  --configuration Release --no-build --no-restore
dotnet test tests/DotNetJq.Cli.Tests/DotNetJq.Cli.Tests.csproj \
  --configuration Release --no-build --no-restore
```

Compatibility changes require the relevant focused tests and differential
oracle. Parser, CLI, GlibcCompat, AOT, packaging, or release changes also require
their specialized checks. Maintainers run the complete release gate before a
release:

```sh
tools/verify-release.sh
```

The full gate needs the pinned upstream checkout and test-only jq oracle; normal
production builds do not. CI provisions those assets automatically.

## Pull requests

Open a PR from any branch in your fork. Explain the upstream behavior, mapped
file/function, exact tests run, manifest/generated-file impact, and any license
or provenance impact. Keep unrelated formatting or refactoring out of a
compatibility fix.

Do not report security vulnerabilities in a public issue. Follow
[SECURITY.md](SECURITY.md).

## Contribution licensing

By submitting a contribution, you agree that original material you author for
the project is licensed under `LICENSE.DotNetJq`, unless the affected file is
explicitly governed by another license. Changes to LGPL or third-party-derived
files remain under their applicable terms and must preserve notices. You must
have the right to submit the contribution.
