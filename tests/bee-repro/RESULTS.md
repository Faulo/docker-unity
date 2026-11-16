# Bee reproduction and Unity upgrade results

Tested on 2026-11-15 for [issue #5](https://github.com/Faulo/docker-unity/issues/5).

## Decision

All fourteen Unity 2021 projects reproduce the Bee hang on **2021.3.45f2**, which
is already the latest public 2021.3 patch. A patch upgrade within Unity 2021
does not provide an escape; this editor line still needs a fix.

**EscortQuest can upgrade from 2022.1.23f1 to 2022.3.62f3.** Its exact issue
revision built a Windows player successfully in 150 seconds with exit code 0.
Only the copied ProjectVersion.txt was changed; Unity's automatic migration
resolved URP 14.0.12 from the original 13.1.8 dependency. No manual source or
package edits were needed. Gameplay was not tested.

The version scope is public releases returned by Unity's release API:
[2021.3](https://services.api.unity.com/unity/editor/release/v1/releases?limit=5&order=RELEASE_DATE_DESC&version=2021.3),
[2022.1](https://services.api.unity.com/unity/editor/release/v1/releases?limit=5&order=RELEASE_DATE_DESC&version=2022.1), and
[2022.3](https://services.api.unity.com/unity/editor/release/v1/releases?limit=5&order=RELEASE_DATE_DESC&version=2022.3).
The responses were saved in `.reports/bee-repro/releases.json`. Enterprise /
Industry extended-LTS releases were outside this matrix. Cross-year upgrades
of the fourteen Unity 2021 games were not tested.

## Per-project results

These fourteen checkouts were clean before and after testing. Their current
editor and latest public patch are both 2021.3.45f2. Times include startup and
compilation followed by a 120-second stable-hang observation window; they do
not include host-side preparation or cleanup.

| Project | Source revision | Cold Windows-player build on Linux | Upgrade decision |
| --- | --- | --- | --- |
| BattleOfTheGods | `ea248964` | Bee stall, 180 s | No newer public 2021 patch |
| BrieYourself | `4499ea31` | Bee stall, 170 s | No newer public 2021 patch |
| ChartRunner | `98eef3cc` | Bee stall, 180 s | No newer public 2021 patch |
| Communiganda | `460867f8` | Bee stall, 140 s | No newer public 2021 patch |
| FindingHome | `9495b3c1` | Bee stall, 150 s | No newer public 2021 patch |
| IAmSusi | `99581400` | Bee stall, 301 s | No newer public 2021 patch |
| KeepEverybodyHappy | `9871b4b5` | Bee stall, 221 s | No newer public 2021 patch |
| NanoMixture | `56223619` | Bee stall, 160 s | No newer public 2021 patch |
| NuttinToLose | `6a6e0570` | Bee stall, 170 s | No newer public 2021 patch |
| Orbital | `2bb81ec3` | Bee stall, 150 s | No newer public 2021 patch |
| ReverseSlender | `a432622a` | Bee stall, 170 s | No newer public 2021 patch |
| SheepThrills | `070f3e77` | Bee stall, 170 s | No newer public 2021 patch |
| SonarUndDochSoFern | `227114cc` | Bee stall, 170 s | No newer public 2021 patch |
| SpaceCape | `f4086932` | Bee stall, 150 s | No newer public 2021 patch |
| EscortQuest | `efcf7991` | 2022.1.23f1: Bee stall; 2022.3.62f3: passed | Recommend 2022.3.62f3 |

EscortQuest was absent from the supplied local directory. Its exact revision
was exported read-only with `git archive` from the existing Mörkö Jenkins
workspace. The export excluded that workspace's modified ProjectSettings.asset.
All actual builds ran locally. Its original 2022.1.23f1 baseline needed the
licensing compatibility setting described below.

## Minimal reproduction and controls

The checked-in fixture has one C# component, one scene, and no third-party
packages. Run it using the commands in [README.md](README.md).

| Editor / launch | Result |
| --- | --- |
| 2021.3.45f2, native unity | Bee stall after 130 s |
| 2021.3.45f2, direct Composer | Bee stall after 140 s |
| 2021.3.45f2, native with licensing compatibility setting | Bee stall after 140 s |
| 2022.1.25f1, default runtime | Licensing client failed before compilation |
| 2022.1.25f1, licensing compatibility setting | Bee stall after 140 s |
| 2022.3.62f3, default runtime | Passed after 30 s; BeeRepro.exe produced |

This reproduces the failure independently of Jenkins and the native supervisor.
The classifier requires a childless Bee with --stdin-canary, exactly two
threads waiting on a futex and a pipe, and an unchanged profiler ending in
ProfilerWriteOutput for at least 120 seconds. General timeouts, crashes, and
infrastructure failures are classified separately. The test does not establish
the precise internal cause of the Bee deadlock.

## Unity 2022.1 caveats

A fresh 2022.1.25f1 editor cannot start its licensing client on this glibc 2.41
image: loading libbindings.so fails because it requests an executable stack.
The diagnostic runs used `GLIBC_TUNABLES=glibc.rtld.execstack=2`, recorded in
their results. This changes only the disposable container environment. Editor
binaries, Bee arguments, published images, and shared editor volumes were not
patched. The same setting still reproduces the 2021 minimal hang.

EscortQuest on 2022.1.25f1 with that setting failed during package resolution
because URP 13.1.8 could not be found, then exited 133. That is **not** counted
as a project-specific Bee reproduction. The minimal fixture independently
proves that this latest 2022.1 patch still exhibits the Bee defect. Moving
EscortQuest to 2022.3.62f3 is the tested successful alternative.

## Environment and evidence

- Published Linux image: `sha256:45211f7757814d485c6c0cf25a8c9b725920639eb0a9009e3efa5476bffaf185` (also the locally reported repository digest).
- Debian 13.7, glibc 2.41-12+deb13u4, WSL2 kernel 6.18.33.2-microsoft-standard-WSL2.
- Explicit Docker context `linux`; Windows **player target**, not Windows host.
- Each case copied Assets, Packages, and ProjectSettings into a fresh container.
  Source checkouts and editor volumes were read-only. No generated Library was
  reused. The normal launcher had UNITY_NO_GRAPHICS=0 and an 1800-second limit.
- Machine-readable case index: `.reports/bee-repro/summary.json`. Each indexed
  evidence directory contains source/image identity, a build log, process/thread
  observations, raw profiler files, and result JSON. Profiler hashes were checked
  against the final observations.
- Completed 2021 game cases are under `matrix/` and `matrix-final/`; EscortQuest
  runs are under `escort-baseline/`, `escort-latest-patch/`, and `escort-lts-final/`.
- Docker transport/mount failures and early fixture-setup failures were retained
  in separate attempt directories and excluded from the conclusions.
- Pester assertions use copies of the twenty selected results under `verified/`:
  eighteen expected failing build contracts and two successful 2022.3 controls.
  The report is `.reports/bee-repro/pester-results.xml`.
- Native Linux servers and Windows-host builds were not rerun: this investigation
  reproduced the reported Linux-host defect locally. Unity player gameplay and
  Enterprise extended-LTS editors were not validated.

No production fix, Git mutation, CI trigger, or deployment was performed.
Ruff, PSScriptAnalyzer, PowerShell parsing, and whitespace checks passed.
Idea MCP inspection was unavailable in this session. All test containers were
removed after collecting their evidence.
The separate `bee-repro-editors-2022` volume retains the installed 2022.1.23f1
and 2022.1.25f1 editors for rerunning these tests.

## 2021.3 remedy investigation (2026-11-15)

The smallest demonstrated workaround is a Linux Bee wrapper that removes
`--stdin-canary`. It fixes both the minimal fixture and BattleOfTheGods on the
same published image, without changing project settings or the Unity version.
Both cases started from a cold Library. The shared editor was copied into
`bee-remedy-editors`; only this disposable copy was changed. Original Bee hash:
`8561ed19e6d35e1e947b450dd528867e7c43c9fe43b5cce9086b58d3cad4fa67`.

| 2021.3.45f2 experiment | Minimal fixture | BattleOfTheGods |
| --- | --- | --- |
| Original Bee, glibc 2.41 (previous baseline) | Bee stall, 130 s | Bee stall, 180 s |
| Remove only `--stdin-canary`, native launch | Player built, 20 s | Player built, 201 s |
| Remove only `--stdin-canary`, direct Composer | Player built, 30 s | Not rerun |
| Keep canary, run Bee alone with glibc 2.31 | Player built, 30 s | Player built, 160 s |

All five successful cases exited zero and produced a nonempty player executable.
Timings include polling and concurrent test activity; they are not benchmarks.
The older-runtime experiment extracted Debian `libc6=2.31-13+deb11u11` and
`libgcc-s1=10.2.1-6` into a separate 13 MiB volume and invoked its dynamic loader
for Bee only. Unity and the rest of the container retained glibc 2.41. This
supports a compatibility problem in the old Bee canary path; it does not
identify a particular glibc commit or establish the exact defective component.

Unity staff describe the canary as the mechanism for cancelling a build from
the editor's progress UI. Removing it bypasses that mechanism; it is not an
unqualified drop-in fix. See the [original Unity discussion](https://discussions.unity.com/t/linux-editor-stuck-on-loading-because-of-bee-backend-w-workaround/854480).

### Cancellation findings

| Signal target while Bee is present | Setup | Result after 20 s |
| --- | --- | --- |
| Native launcher | Original editor, minimal | Launcher exited 130; Unity and Bee survived |
| Native launcher | No-canary wrapper, BattleOfTheGods | Launcher exited 130; Unity and Bee survived |
| Unity directly | No-canary wrapper, minimal | No Unity or Bee remained |
| Unity directly | Isolated glibc 2.31, minimal | No Unity or Bee remained |

The original-editor case establishes a cleanup failure independent of the
workaround. In that run Unity PID/PGID was 185 and Bee PID 337 shared PGID 185;
Unity was reparented to PID 1 after launcher cancellation. The wrapped run
showed the same pattern with Unity PID/PGID 180 and Bee PID 337. The native
supervisor creates a process group for Composer, but Unity appears in a
separate process group. `UnixProcessTree.TerminateAsync` currently signals
only the recorded group and returns as soon as the root process exits, skipping
the later SIGKILL. Both behaviors need to be covered by a cleanup fix.

The direct-editor probes only establish absence of Unity/Bee at the 20-second
observation. They can signal a brief frontend invocation, so they do not prove
instant cleanup of arbitrary compiler children or preservation of progress-bar
cancellation. Every case's container was removed as the final cleanup guard.

### Proposed production implementation

1. Ship the small argument-preserving wrapper in the Linux image. Limit initial
   application to Linux Unity 2021.3, with 2021.3.45f2 as the verified version.
   Keep the original binary and preserve exit status using `exec`.
2. Apply it immediately before editor execution, covering cached editors and
   editors freshly installed by Hub. The shared backend's `UnityEditor::execute`
   is the appropriate common integration point for native CLI and direct
   Composer calls. An image-build-only or container-startup patch misses editors
   downloaded later; changing only the native launcher misses direct Composer.
3. Make preparation locked, atomic, idempotent, and recognizable by a wrapper
   marker. Never rename an existing wrapper as the original. Detect Hub repairs
   and unknown preexisting replacements. Give an actionable error if an
   unprepared editor is read-only; already prepared read-only editors should work.
   Document that the compatibility wrapper persists in the editor cache.
4. Fix invocation cleanup to include Unity's separate process group and compiler
   descendants, with bounded TERM/KILL escalation that still runs after the
   Composer root exits. Scope this to the invocation and verify process identity;
   do not kill a shared sidecar or unrelated Unity jobs. The precise containment
   mechanism still needs implementation and testing.
5. Promote the cold minimal build and cancellation cases to integration coverage.
   Verify first/cached installs, repeat preparation, argument and error forwarding,
   timeout and manual abort during actual compilation, concurrent job isolation,
   all fourteen affected games, modern Linux editors, and unchanged Windows behavior.

Prefer the small wrapper plus proper invocation cleanup for this batch-build
image. The isolated older libc is a demonstrated fallback if preserving the
original canary is required, but introduces another legacy runtime to maintain.
It does not fix the independently observed launcher cancellation failure.
No whole-image downgrade or system-wide `LD_LIBRARY_PATH` change is needed.

Evidence is under `.reports/bee-remedy/`: `without-canary/`,
`without-canary-composer/`, `legacy-libc/`, and the four `*-cancel-*` directories.
Each retains source and image identity, build logs, process observations, and
result JSON. `compat-runtime.txt` records the older runtime's identity and hashes.
Pester checked 18 assertions: 16 passed, with the two launcher-cleanup assertions
failing for the expected surviving Unity/Bee processes. All ten build assertions
passed. The wrapper contract test passed six argument cases, including PID and
nonzero exit-code preservation. Ruff and PSScriptAnalyzer passed. Idea MCP was
unavailable; the available Rider analysis tool reported these scripts were not
included in its open solution and could not analyze them.

This remains a local Linux experiment. Garl/Popo integration, a production image
build, Windows-host validation, the other thirteen games with the workaround,
and the proposed installer/cleanup implementation have not been performed in
this phase. No Git mutation, CI trigger, or publication occurred. Private editor
and compatibility-runtime volumes are retained for repeat runs.
