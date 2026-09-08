# DotNetJq.GlibcCompat

This replaceable compatibility assembly contains the modified managed GNU C
Library 2.39 translations needed to reproduce jq's historical Linux `gamma`,
`lgamma`, `lgamma_r`, and `tgamma` results bit for bit. It has no native,
P/Invoke, process, or external jq dependency.

The corresponding source is pinned to GNU C Library tag `glibc-2.39` (tag
object `9609a435f3f9a07c1cf607ad5821b12f735abd69`), release commit
`ef321e23c20eebc6d6fb4044425c00e6df27b05f`. The translated source files are
listed in the license/provenance notice at the top of each corresponding C#
source file. They cover the gamma/lgamma core, the gamma-private exp, exp2,
expm1 and pow paths, and the `ldbl-96/gamma_product.c` binary80 evaluation
used by the pinned x86-64 jq oracle.

The NuGet package contains this complete corresponding source at
`lgpl-source/src/DotNetJq.GlibcCompat/`, together with `Directory.Build.props`
and `global.json`. From the extracted package root, restore and build it with:

```sh
dotnet restore lgpl-source/src/DotNetJq.GlibcCompat/DotNetJq.GlibcCompat.csproj \
  --configfile lgpl-source/src/DotNetJq.GlibcCompat/NuGet.Offline.Config \
  -p:IsAotCompatible=false -p:IsTrimmable=false \
  -p:EnableAotAnalyzer=false -p:EnableTrimAnalyzer=false
dotnet build lgpl-source/src/DotNetJq.GlibcCompat/DotNetJq.GlibcCompat.csproj \
  --configuration Release --no-restore \
  -p:IsAotCompatible=false -p:IsTrimmable=false \
  -p:EnableAotAnalyzer=false -p:EnableTrimAnalyzer=false
```

When building from the repository rather than an extracted package, omit the
`lgpl-source/` prefix. The required SDK version is recorded in
`lgpl-source/global.json`; the explicitly selected `NuGet.Offline.Config`
clears all package sources because no third-party NuGet package is required.
Keeping it opt-in prevents the component's offline-only policy from suppressing
the NativeAOT packs needed when the complete CLI graph is restored. The
component itself therefore restores and builds without network access.
The four property overrides above affect only build-time analyzer-pack
resolution for this deliberately offline reconstruction. They do not change
the source or runtime behavior of the replacement assembly. Normal repository,
package, and NativeAOT builds retain `IsAotCompatible=true` and
`IsTrimmable=true`, run the analyzers, and execute the published native smoke.

The replaceable ABI is the public static
`DotNetJq.GlibcCompat.GlibcCompatMath` class with these methods:

- `double Gamma(double value)`
- `double Lgamma(double value)`
- `(double Value, int Sign) LgammaR(double value)`
- `double Tgamma(double value)`

Both this assembly and `DotNetJq.dll` declare the .NET SDK
`IsAotCompatible` and `IsTrimmable` contracts and build with the corresponding
analyzers enabled. The library package places the generated API documentation
beside the assembly as `lib/net10.0/DotNetJq.GlibcCompat.xml`. Those docs make
the historical ABI distinction explicit: `Gamma` and `Lgamma` return
`log(abs(Gamma(value)))`; `Tgamma` returns `Gamma(value)`; and `LgammaR` also
returns the gamma sign.

To install a modified build, keep the assembly name
`DotNetJq.GlibcCompat`, assembly version `1.0.0.0`, and those public
signatures, then replace
`DotNetJq.GlibcCompat.dll` beside the consuming application's `DotNetJq.dll`.
The main assembly references only this public ABI and contains none of the
component's algorithms or coefficient tables, so rebuilding or replacing this
DLL does not require changing `DotNetJq.dll`.

Reverse engineering of the combined work for debugging modifications to this
component is permitted. No DotNetJq package term restricts the exercise of
rights granted for this component by the LGPL.

This component is licensed under LGPL-2.1-or-later. See `COPYING.LIB`.
