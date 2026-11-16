# Legacy Linux Bee reproduction

Run from the repository root with Python 3 and the local `linux` Docker context:

```console
python tests/bee-repro/run_matrix.py --projects BattleOfTheGods
```

Without `--projects`, the runner tests the fourteen Unity 2021 projects from
issue #5. `--projects-root` defaults to `~/Desktop/Unity`; project directories
are named `GameJam.<name>`. Each source checkout is mounted read-only. Only
`Assets`, `Packages`, and `ProjectSettings` are copied to a fresh container layer,
so each test has a cold Library. Local files, including uncommitted changes,
are used; the source commit and working-tree status are recorded.

On a native WSL Docker daemon with Windows drives mounted under `/mnt`, pass
`--wsl-paths` to use daemon-native bind paths rather than Docker Desktop path
translation. This option is not intended for remote engines or arbitrary
Docker Desktop filesystem layouts. Transient inspection transport errors are
retried; infrastructure failures are saved separately and must not be counted
as Bee failures.

Prerequisites are the published `faulo/unity:latest` Linux image, the
`unity-binaries` volume containing the requested Linux editor and Windows Mono
build module, and a valid license in `unity-license`. The four credential
environment variables documented in the root README are forwarded by name.
Editor and license seed volumes are mounted read-only; the license is copied
into the disposable container. Missing editors cannot be installed into the
read-only editor volume. Install prerequisites separately before testing.

The image tag is resolved to an immutable image ID before the matrix starts.
The runner executes `unity build --junit - /work/project /work/output windows`
with `UNITY_NO_GRAPHICS=0`, matching the reported batch-mode invocation. Use
`--launch composer` to bypass the native supervisor or `--version VERSION` to
change only the copied project's editor version. An override does not upgrade
the source checkout.

Results are written under `.reports/bee-repro/<project>-<launch>-<version>`.
Existing evidence directories are never overwritten. Use `--results` for
independent repeats. Each case includes source/image identity, a redacted build
log, process/thread observations, raw Bee profiler files, and a JSON result.

For a small standalone fixture, independent of the game checkouts:

```console
python tests/bee-repro/run_matrix.py --projects-root tests/bee-repro/fixtures --projects Minimal --results .reports/bee-minimal
python tests/bee-repro/run_matrix.py --projects-root tests/bee-repro/fixtures --projects Minimal --results .reports/bee-minimal --version 2022.3.62f3
```

The fixture contains one C# component and one scene. The version override also
records the official changeset from Unity's release API, as required by the
command backend. `--editor-volume` selects an alternative preinstalled editor
volume. These tests target Linux hosts building Windows players; a Windows-host
run does not reproduce the reported defect.

Unity 2022.1.25f1's freshly installed licensing client cannot load its
`libbindings.so` under the tested glibc 2.41 because that library requests an
executable stack. Preserve a default run first. `--execstack-compat` explicitly
sets `GLIBC_TUNABLES=glibc.rtld.execstack=2` for a separate diagnostic run; the
setting is recorded in source and result JSON. It changes no editor binaries,
shared volumes, or image. A result with this option must be distinguished from
the default runtime. The glibc compatibility mechanism is documented in
[Debian's glibc release notes](https://sources.debian.org/src/glibc/2.42-17/debian/debhelper.in/libc.NEWS).

To assert the build contract on the collected results with Pester 5:

```powershell
$container = New-PesterContainer -Path tests/bee-repro/Bee.Repro.Tests.ps1 -Data @{
    ResultsPath = (Resolve-Path .reports/bee-minimal).Path
}
Invoke-Pester -Container $container -Output Detailed
```

The assertion deliberately fails for `bee_stall` and other unsuccessful builds.
The reproduction runner itself collects observations; use the Pester result as
the regression-test pass/fail signal.

Verdicts:

- `bee_stall`: Bee with `--stdin-canary` has exactly two threads, a futex waiter
  and a pipe reader, no children, and an unchanged profiler ending in
  `ProfilerWriteOutput` for at least 120 seconds.
- `passed`: command exits zero and produces a Windows executable.
- `build_failed`: command exits without satisfying the player-build contract.
- `timeout_unclassified`: the 30-minute deadline expires without the specific
  Bee signature. This is not evidence of the Bee defect.

`--stall-seconds` and `--timeout` adjust those bounds. The host also enforces an
outer deadline covering preparation and always removes its own container and
anonymous volumes. Cleanup targets only that case's container, never a shared
sidecar or unrelated process group. No Docker socket is exposed to the case.

The recorded post-termination snapshot is diagnostic only: it is taken after
terminating the launcher and before container teardown. It does not establish
whether Jenkins cancellation or a particular supervisor version owns a cleanup
failure. Successful player creation also does not establish gameplay correctness.

See [the recorded investigation results](RESULTS.md) for the tested versions,
project revisions, and upgrade recommendations.

## Experimental 2021.3 remedies

These scripts are investigation fixtures, not the production installation hook.
They leave the published image and shared editor volume unchanged. To prepare
a new private editor volume, mount the scripts at `/repro` and run:

```powershell
docker --context linux run --rm --no-healthcheck `
    --mount type=volume,source=unity-binaries,target=/source,readonly `
    --mount type=volume,source=bee-remedy-editors,target=/candidate `
    --mount "type=bind,source=$((Resolve-Path tests/bee-repro).Path),target=/repro,readonly" `
    faulo/unity:latest sh /repro/prepare_remedy.sh
```

On this machine's native WSL daemon, use the `/mnt/c/.../tests/bee-repro` source
path instead of the Windows bind path. Preparation refuses to replace an
existing candidate editor. The retained `bee-remedy-editors` volume is already
prepared. The original Bee is preserved as `bee_backend.real`; its SHA-256
must match the shared editor's original. The wrapper removes only the exact
`--stdin-canary` argument, preserves all other arguments, and uses `exec`.

```console
python tests/bee-repro/run_matrix.py --projects-root tests/bee-repro/fixtures --projects Minimal --results .reports/bee-remedy-repeat/minimal --editor-volume bee-remedy-editors --bee-wrapper tests/bee-repro/bee_without_canary.sh --wsl-paths
python tests/bee-repro/run_matrix.py --projects BattleOfTheGods --results .reports/bee-remedy-repeat/game --editor-volume bee-remedy-editors --bee-wrapper tests/bee-repro/bee_without_canary.sh --wsl-paths
```

`--bee-wrapper` optionally overlays a script at the selected editor's Bee path,
records its text and hash, and requires the private editor's `.real` backup.
`--compat-volume` mounts an isolated runtime read-only at `/compat`.
The diagnostic `bee_legacy_libc.sh` uses that runtime for Bee alone and forwards
the canary argument unchanged. The retained `bee-remedy-libc` volume contains
the extracted Debian Bullseye packages `libc6=2.31-13+deb11u11` and
`libgcc-s1=10.2.1-6`, downloaded from Debian's Bullseye repository. Nothing was
installed over the image's system libc. Runtime versions and hashes are in
`.reports/bee-remedy/compat-runtime.txt`. To repeat this alternative:

```console
python tests/bee-repro/run_matrix.py --projects-root tests/bee-repro/fixtures --projects Minimal --results .reports/bee-remedy-repeat/libc --editor-volume bee-remedy-editors --bee-wrapper tests/bee-repro/bee_legacy_libc.sh --compat-volume bee-remedy-libc --wsl-paths
```

Use `--cancel-at-bee launcher` or `--cancel-at-bee editor` in a separate results
directory to send SIGTERM when Unity and Bee are first observed together.
These runs poll every 100 ms and record both the pre-signal state and survivors
after 20 seconds. The first Bee invocation can be a short frontend probe;
these checks do not establish cancellation of long compiler subprocesses or
editor progress-bar cancellation. `cancellation_not_triggered` means the
requested signal opportunity was missed and must fail the cancellation check.
Run `Bee.Cancellation.Tests.ps1` with `ResultsPath` for these cases instead of
the successful-build assertions. `cancelled_with_survivors` deliberately fails.

Run `python3 /repro/test_bee_wrapper.py` inside a Linux container with the scripts
mounted at `/repro` to check argument quoting, exact flag filtering, unchanged
PID through `exec`, and propagation of a nonzero Bee exit status.
