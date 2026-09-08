# Third-party notices

## jq

DotNetJq is a managed reimplementation whose behavior, jq-shaped compatibility APIs, and mapped source layout are derived from jq 1.8.2, commit 34f7186b86743a083a589741b6cea95293524108. The embedded `src/DotNetJq/Resources/builtin.jq` resource is reused byte-for-byte from that pinned jq release.

jq is copyright Stephen Dolan and contributors and is licensed under the MIT License. The complete pinned upstream notice, including the additional notices for material incorporated by jq, is preserved as `COPYING.jq` in the repository and shipped as `COPYING` in the package.

## decNumber

The jq source tree contains IBM decNumber material distributed under the ICU License. DotNetJq does not compile or link the native C implementation. Its managed numeric compatibility layer maps the decNumber API, constants, status flags, and behavior used by jq. The mapped managed files preserve the applicable IBM copyright and ICU license identification, and the complete ICU terms are included in repository `COPYING.jq` and package `COPYING`.

## Oniguruma

DotNetJq does not compile or link the native Oniguruma library. Its managed
regular-expression compatibility implementation nevertheless contains
`PORT` and `GENERATED` material derived from the Oniguruma sources pinned by
jq 1.8.2: Oniguruma commit
`4ef89209a239c1aea328cf13c05a2807e5c146d1` (v6.9.10). The managed engine uses
`System.Text.RegularExpressions` behind jq-shaped compatibility interfaces,
while the ported callout parsing, diagnostics, case folding, Unicode property
data, text segmentation, and related compatibility logic retain their mapped
Oniguruma provenance.

Oniguruma is copyright (c) 2002-2021 K. Kosako and is distributed under a
BSD-style two-clause license. The exact pinned `vendor/oniguruma/COPYING` file
is distributed as `THIRD_PARTY_LICENSES/Oniguruma-License.txt` in both the
repository and the managed-library, CLI-tool, and standalone NativeAOT
distributions.

## Gardens Point parser and lexer generators

The repository pins the `Springcomp.GPPG` and `Springcomp.GPLEX` 1.2.5
.NET tools for development-time generation of the managed jq parser and
lexer. These tools are modernized distributions of the Gardens Point Parser
Generator and Gardens Point LEX. The generator executables and NuGet packages
are not linked into or shipped as dependencies of `DotNetJq.dll`.

`DotNetJq.dll` does contain a source-integrated subset of the
`Springcomp.GPPG.Runtime` 1.2.5 implementation at commit
`f4634057620757a38789b4d53df73817667d1842` required by the generated parser:
`AbstractScanner.cs`, `IMerge.cs`, `PushdownPrefixState.cs`, `Rule.cs`,
`ShiftReduceParser.cs`, and `State.cs`. These files retain their upstream
copyright notices, are internal to the DotNetJq assembly, and do not require or
load a separate GPPG runtime assembly. The complete GPPG BSD license is
distributed as `THIRD_PARTY_LICENSES/GPPG-License.md` in both the repository
and package.

Gardens Point Parser Generator is copyright (c) 2005-2014 Queensland
University of Technology (QUT), Wayne Kelly, John Gough, and contributors.
Gardens Point LEX is copyright (c) 2006-2014 Queensland University of
Technology (QUT), John Gough, and contributors. Redistribution and use are
permitted under their BSD licenses, subject to preservation of the applicable
copyright notices, conditions, and disclaimers. The generator projects state
that generated output is the property of the grammar specification owner.

The managed grammar sources identify the exact jq 1.8.2 files and revision
from which they were ported. The original C-action Flex/Bison files are not
duplicated in this repository.

## fdlibm

The managed `erf`, `erfc`, `acosh`, `asinh`, `atanh`, `expm1`, `log1p`, Bessel, and related special-function compatibility code contains translations of algorithms from fdlibm.

Copyright (C) 1993 by Sun Microsystems, Inc. All rights reserved.

Developed at SunSoft, a Sun Microsystems, Inc. business. Permission to use, copy, modify, and distribute this software is freely granted, provided that this notice is preserved.

## GNU C Library gamma compatibility component (LGPL-2.1-or-later)

The framework-dependent `DotNetJq.Library` library package contains one separately
built and replaceable LGPL component:
`lib/net10.0/DotNetJq.GlibcCompat.dll`. Its corresponding modified source is
shipped under `lgpl-source/src/DotNetJq.GlibcCompat/`. The main
`lib/net10.0/DotNetJq.dll` assembly contains only a thin runtime ABI bridge to
this component; it does not contain the component's gamma algorithms or
coefficient tables.

The self-contained NativeAOT `dotnetjq` CLI has a different distribution
boundary: NativeAOT statically incorporates the GlibcCompat code into the
platform executable. The .NET tool pointer package and every RID-specific tool
package include the complete buildable CLI and library source under
`aot-source/`. Each standalone NativeAOT binary archive is distributed beside
the version-matched deterministic `dotnetjq-aot-source-VERSION.tar.gz` archive
and its `.sha256` file.

`DotNetJq.GlibcCompat` is a managed translation of gamma-family and supporting
exponential code from GNU C Library 2.39, tag `glibc-2.39` (tag object
`9609a435f3f9a07c1cf607ad5821b12f735abd69`), release commit
`ef321e23c20eebc6d6fb4044425c00e6df27b05f`. The translated source maps these
upstream files:

- `sysdeps/ieee754/dbl-64/e_gamma_r.c`
- `sysdeps/ieee754/ldbl-96/gamma_product.c`
- `sysdeps/ieee754/dbl-64/lgamma_neg.c`
- `sysdeps/ieee754/dbl-64/lgamma_product.c`
- `math/mul_split.h`
- `sysdeps/ieee754/dbl-64/e_exp.c`
- `sysdeps/ieee754/dbl-64/e_exp_data.c`
- `sysdeps/ieee754/dbl-64/s_expm1.c`
- `sysdeps/ieee754/dbl-64/e_pow.c`
- `sysdeps/ieee754/dbl-64/e_pow_log_data.c`
- `sysdeps/ieee754/dbl-64/e_exp2.c`
- the fdlibm-derived `sysdeps/ieee754/dbl-64/e_lgamma_r.c` integration

Copyright (C) 1997-2024 Free Software Foundation, Inc.

Copyright (C) 2013-2024 Free Software Foundation, Inc.

Copyright (C) 2015-2024 Free Software Foundation, Inc.

Copyright (C) 2018-2024 Free Software Foundation, Inc.

The `DotNetJq.GlibcCompat` assembly and its corresponding modified source are
licensed under the GNU Lesser General Public License as published by the Free
Software Foundation, either version 2.1 or (at your option) any later version.
The exact GNU C Library 2.39 `COPYING.LIB` is included at the package root and
inside the applicable managed or NativeAOT corresponding-source directory.

The managed package source directory contains the complete component source,
project file, and build/replacement instructions. Users may rebuild it and
replace `DotNetJq.GlibcCompat.dll` beside `DotNetJq.dll` with an ABI-compatible
modified version without modifying or relinking `DotNetJq.dll`.

The NativeAOT corresponding source contains the complete three-project source
graph, checked-in generated inputs, .NET 10 build selection, license material,
and deterministic archive/rebuild/proof tools. `porting/AOT_RELINKING.md`
provides exact commands for all eight supported 64-bit RIDs. The automated
Linux proof hides the development and upstream jq checkouts, disables network
access, rebuilds from the distributed source, changes only the LGPL-derived
GlibcCompat `Gamma` implementation, and verifies a different jq-visible result
and native executable hash after relinking.

Reverse engineering of either combined work for debugging modifications to
this LGPL component is permitted; no DotNetJq package term restricts that
permission or any right granted by the LGPL.

## .NET NativeAOT runtime

RID-specific `dotnetjq` tool packages and standalone NativeAOT archives contain
code statically incorporated from the .NET runtime pack selected by the .NET
SDK for that RID. Each such binary distribution includes `LICENSE.TXT` and
`THIRD-PARTY-NOTICES.TXT` copied byte-for-byte from that exact resolved
`Microsoft.NETCore.App.Runtime.NativeAOT.<rid>` pack. Release verification
resolves the same pack independently and rejects an absent or modified copy.
The RID-selector package contains no runtime executable and therefore does not
substitute one RID's runtime notices for another's.

## Test fixtures

Official jq test fixtures are read unchanged from the separately pinned jq checkout during compatibility testing. They are not copied into the DotNetJq product assembly or package and are governed by the notices in that checkout.
