# Third-party notices

The release package bundles the **official, unmodified BepInEx 5.4.22 x64 build**
(<https://github.com/BepInEx/BepInEx/releases/tag/v5.4.22>, SHA-256 of the zip checked by `tools/MakeRelease.ps1`).
Full license texts are in [`release/licenses`](release/licenses) and inside every release zip.

| Component | Files in the package | License | Source code |
|---|---|---|---|
| BepInEx 5.4.22 | `BepInEx/core/BepInEx*.dll` | LGPL-2.1 | <https://github.com/BepInEx/BepInEx> |
| UnityDoorstop | `winhttp.dll`, `doorstop_config.ini` | LGPL-2.1 | <https://github.com/NeighTools/UnityDoorstop> |
| HarmonyX | `BepInEx/core/0Harmony.dll`, `HarmonyXInterop.dll` | MIT | <https://github.com/BepInEx/HarmonyX> |
| Harmony 2 | `BepInEx/core/0Harmony20.dll` | MIT | <https://github.com/pardeike/Harmony> |
| MonoMod | `BepInEx/core/MonoMod.*.dll` | MIT | <https://github.com/MonoMod/MonoMod> |
| Mono.Cecil | `BepInEx/core/Mono.Cecil*.dll` | MIT | <https://github.com/jbevain/cecil> |

The LGPL-2.1 components are used as separate, unmodified libraries. Their source code is available at the links above,
and you may replace them with any compatible build.

*Cuphead* and all its content are the property of StudioMDHR Entertainment Inc. They are not included and not covered by these licenses.
