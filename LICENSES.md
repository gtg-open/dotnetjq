# DotNetJq package licenses

No single license applies to every file in this package.

- Original DotNetJq code, tests, documentation, and tooling authored for this
  project are licensed under the MIT License in `LICENSE.DotNetJq`, except
  where a file or the notices below identify different terms. This grant does
  not relicense material derived from jq, GNU C Library, Oniguruma, GPPG/GPLEX,
  IBM decNumber, fdlibm, or the .NET runtime.
- In the framework-dependent `DotNetJq.Library` library package,
  `lib/net10.0/DotNetJq.GlibcCompat.dll` is a separate, replaceable component
  licensed under the GNU Lesser General Public License, version 2.1 or (at
  your option) any later version (`LGPL-2.1-or-later`). Its complete
  corresponding source is included under `lgpl-source/`; the exact license is
  `COPYING.LIB`.
- A NativeAOT `dotnetjq` executable statically incorporates that component.
  The .NET tool pointer package and every RID-specific tool package therefore
  include the complete CLI and library corresponding source under
  `aot-source/`. Standalone NativeAOT binary archives are distributed beside
  the version-matched deterministic
  `dotnetjq-aot-source-VERSION.tar.gz` archive and its `.sha256` file. Exact
  eight-RID rebuild instructions and the automated modified-component relink
  proof are in `aot-source/porting/AOT_RELINKING.md` in each tool package and
  `porting/AOT_RELINKING.md` inside the source archive.
- Ported, reused, generated, and third-party material is governed by the
  component-specific terms and notices in `COPYING`, `COPYING.jq`, and
  `THIRD_PARTY_NOTICES.md`. `COPYING` preserves jq's MIT license and the other
  notices distributed by the pinned jq 1.8.2 source release. The BSD license
  for the source-integrated GPPG runtime subset is included as
  `THIRD_PARTY_LICENSES/GPPG-License.md`. The BSD license for managed
  `PORT`/`GENERATED` material derived from the pinned Oniguruma sources is
  included as `THIRD_PARTY_LICENSES/Oniguruma-License.txt`.
- Each RID-specific NativeAOT tool package and standalone NativeAOT archive
  carries `LICENSE.TXT` and `THIRD-PARTY-NOTICES.TXT` copied byte-for-byte from
  the exact .NET NativeAOT runtime pack selected by the SDK for that RID. The
  pointer package contains no runtime binary and does not claim a single
  cross-RID runtime notice payload.

The LGPL component boundary, upstream source mapping, managed-DLL replacement
procedure, NativeAOT rebuild/relink procedure, and reverse-engineering
permission are documented in `THIRD_PARTY_NOTICES.md`,
`lgpl-source/src/DotNetJq.GlibcCompat/README.md`, and
`aot-source/porting/AOT_RELINKING.md` as applicable to the distribution form.
Nothing in the DotNetJq package terms restricts reverse engineering of the
combined work for debugging modifications to the LGPL component or any other
right granted for that component by the LGPL.
