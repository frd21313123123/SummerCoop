# MWCoop source provenance

Upstream: https://github.com/Parricidium/MWCoop

Imported revision: `25f322a749f53f2dca16b09c14165ae64709a9db`, upstream version **0.95.3**. Imported on 15 October 2026 for the user's requested My Summer Car adaptation.

The runtime under `src/MWCoop`, resources under `assets` and the original launcher sources are from that revision. The original README and its model/asset credits are retained. MSC changes are applied directly to the runtime at its game boundaries. `../../src/MSC` contains the MSC integration; `../../src/Game/NativeAssembly.cs` and `NativeActionReader.cs` supply fixes from the existing SummerCoop project.

The native MWC loader, graphics plugin and updater are not built by this project's default build. MSCLoader loads the adapted runtime. The retained C++ launcher supplies the reference/source for the geometry, palette, procedural landscape and glass drawing adapted in `../../launcher/MwStyle.cs` and `MwLauncherView.cs`. The bundled launcher keeps the MSC installer and Steam invitation backend.

The MSC route in `Sync/Settings.cs` uses `MscTuning`: stable per-FSM identities,
absolute native tuning commits and revisioned edits. `MscParts` adapts Stage bolts,
their native Tightness/Bolted totals, completed wiring connections and numbered
alternator belts. These local changes use network version 1042 (SummerCoop 0.3.0).

No LICENSE file was present in the imported revision. The repository's MIT license does not relicense upstream code or assets. See the original README for third-party asset attribution. No upstream binaries or game assemblies were imported.
