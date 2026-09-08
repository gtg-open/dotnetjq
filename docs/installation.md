# Installation

DotNetJq 1.0.0 is the first planned public release. Until the version is visible
on the linked package or release page, use the source-build instructions below.

## .NET library

The package is [DotNetJq.Library](https://www.nuget.org/packages/DotNetJq.Library).
It targets `net10.0` and contains `DotNetJq.dll` plus the separately replaceable
`DotNetJq.GlibcCompat.dll`.

```sh
dotnet add package DotNetJq.Library --version 1.0.0
```

Applications using this package require a compatible .NET 10 runtime unless
the application itself is published self-contained or with NativeAOT.

## .NET CLI tool

The package and installed command are both `dotnetjq`:

```sh
dotnet tool install --global dotnetjq --version 1.0.0
dotnetjq --version
```

The tool installer requires a compatible .NET SDK. The selected RID package
contains a self-contained NativeAOT executable, so running the installed command
does not require a separately installed .NET runtime.

Update or remove it with normal .NET tool commands:

```sh
dotnet tool update --global dotnetjq
dotnet tool uninstall --global dotnetjq
```

## Standalone NativeAOT archives

Releases publish `.zip` files for Windows and `.tar.gz` files for Unix. Choose
the archive matching your OS, C library, and architecture from the
[GitHub Releases page](https://github.com/gtg-open/dotnetjq/releases).

```sh
version=1.0.0
rid=linux-x64
asset="dotnetjq-${version}-${rid}.tar.gz"

curl -fLO \
  "https://github.com/gtg-open/dotnetjq/releases/download/v${version}/${asset}"
mkdir "dotnetjq-${version}"
tar -xzf "$asset" -C "dotnetjq-${version}"
"./dotnetjq-${version}/dotnetjq" --version
```

Windows PowerShell:

```powershell
$version = '1.0.0'
$destination = Join-Path $env:LOCALAPPDATA 'Programs\dotnetjq'
Invoke-WebRequest `
  "https://github.com/gtg-open/dotnetjq/releases/download/v$version/dotnetjq-$version-win-x64.zip" `
  -OutFile dotnetjq.zip
Expand-Archive .\dotnetjq.zip $destination
& "$destination\dotnetjq.exe" --version
```

Keep the license and notice files distributed beside the executable. The
separate `dotnetjq-aot-source-VERSION.tar.gz` is corresponding source, not a
runnable archive.

## Homebrew and WinGet

Stable releases are prepared for a project tap and the WinGet community
repository. These commands become available after their first downstream
publication:

```sh
brew install gtg-open/tap/dotnetjq
```

```powershell
winget install --id GtGOpen.DotNetJq --exact
```

Package-manager publication follows the GitHub release; it may appear later
because the WinGet submission is reviewed by Microsoft. There is no apt
repository in the initial distribution plan.

## Platform requirements

| RID | Runtime requirements |
|---|---|
| `win-x64`, `win-arm64` | Supported Windows; OS ICU or Windows NLS fallback |
| `linux-x64`, `linux-arm64` | glibc 2.39 or newer and system ICU |
| `linux-musl-x64`, `linux-musl-arm64` | musl/Alpine and `icu-libs` |
| `osx-x64`, `osx-arm64` | Supported macOS and OS globalization support |

Self-contained means the .NET runtime is included. It does not mean libc, ICU,
or every operating-system dependency is statically bundled.

## Build from source

The exact stable SDK is pinned in `global.json`:

```sh
git clone https://github.com/gtg-open/dotnetjq.git
cd dotnetjq
dotnet restore DotNetJq.sln
dotnet build DotNetJq.sln --configuration Release --no-restore
dotnet run --project src/DotNetJq.Cli --configuration Release -- --version
```

The upstream jq checkout and native oracle are required only by compatibility
and differential tests, not by production builds or runtime use.
