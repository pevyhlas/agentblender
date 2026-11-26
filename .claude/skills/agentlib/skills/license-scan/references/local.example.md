# license-scan: local settings (example)

Copy this file to `references/local.md` in the vendored skill and edit it to the repository's
target license. Library updates never read, write or remove `local.md`. Without it the script
lists each dependency's license but classifies none.

Each setting is a line `key: item, item, ...`, optionally as a list item; a key may repeat.
Items are case-insensitive globs (`*` matches anything). Commas separate items; an item without
`WITH` may also be split by spaces.

## License lists

A dependency's license is an SPDX expression. Each license id in it is looked up here; when an
id matches several lists, the strictest wins (deny, then review, then allow). An id on no list is
`unlisted`. `OR` takes the best option (allowed, review, unlisted, denied), `AND` the worst.
`X WITH exception` is looked up as written first; when that pair is on no list it takes `X`'s
class, except that a denied `X` becomes review, since an exception only adds permissions.

This example suits a permissively licensed (MIT or Apache-2.0) repository that ships binaries:

- allow: MIT, MIT-0, Apache-2.0, BSD-2-Clause, BSD-3-Clause, ISC, 0BSD, Zlib, PostgreSQL, Unlicense, CC0-1.0, BSL-1.0, Python-2.0
- allow: GPL-2.0-only WITH Classpath-exception-2.0
- review: LGPL-*, MPL-2.0, EPL-*, MS-PL, MS-RL, CDDL-*, OFL-1.1, CC-BY-4.0, CC-BY-SA-*, UNLICENSED, LicenseRef-*
- deny: GPL-*, AGPL-*, SSPL-*, BUSL-*, CC-BY-NC-*, Elastic-2.0, Commons-Clause

How common expressions come out with these lists:

| expression | class |
|---|---|
| `MIT` | allowed |
| `(MIT OR GPL-3.0-only)` | allowed (the MIT option) |
| `(MIT AND LGPL-2.1-or-later)` | review |
| `GPL-3.0-or-later` | denied |
| `GPL-2.0-only WITH Classpath-exception-2.0` | allowed (the pair is listed) |
| `GPL-3.0-only WITH GCC-exception-3.1` | review (denied base, exception not listed) |
| `SEE LICENSE IN LICENSE.md` | unparsed |

## Package rules

A package rule overrides the license class for packages whose name matches; prefix the glob with
the ecosystem (`npm:`, `nuget:`, `pypi:`) to limit it to one. Use `allow-packages` for a package
the owner has checked by hand whose license the script cannot resolve offline.

- deny-packages: nuget:SixLabors.*
- review-packages: npm:@fortawesome/*
- allow-packages: nuget:xunit.abstractions

## Vendored files

The script lists files added under a vendor directory (`vendor/`, `third_party/`, `external/`,
`wwwroot/lib/` and similar) and fonts, 3D models, textures and audio anywhere, with the nearest
license file next to them. These keys add to the defaults.

- vendored-dirs: art/licensed/*
- vendored-extensions: .png, .svg
- not-vendored: art/generated/*
- license-files: provenance.json, *.provenance.json
