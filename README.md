# Unity Docker image

Cross-platform Unity build and test image for Linux and Windows. Published images are available as `faulo/unity`.

## Included tools

Both variants include Unity Hub, the `unity` command stack, Git and Git LFS, PHP 8.4 and Composer, .NET SDK and DocFX, Python, FFmpeg, Blender, Nano, and Zip. Windows also includes PowerShell. The Linux base packages are declared in [`linux/unity.packages`](linux/unity.packages); Windows dependencies are declared in [`windows/unity.nuspec`](windows/unity.nuspec). Windows installs the current official PECL IMAP build because PHP 8.4 no longer bundles it.

Node.js, npm, Butler, and SteamCMD are intentionally not included.

## Image variants

- `faulo/unity:latest` is the primary multi-platform image.
- `faulo/unity:latest-linux` targets the complete Linux platform set.
- `faulo/unity:latest-windows` targets every published Windows base variant.
- `faulo/unity:latest-windows-ltsc2019` targets Windows Server 2019 specifically.

The `latest-*` tags are compatibility aliases for clients that need to avoid resolving unrelated platforms from the primary manifest. Integration tests normally target `latest`. Linux hosts require an amd64 Docker daemon. Windows images require a compatible Windows Server 2019 daemon.

## Build arguments

| Argument | Default | Purpose |
| --- | --- | --- |
| `UNITY_TIMEOUT` | `14400` | Configures Composer's child-process timeout in seconds. |
| `DOTNET_VERSION` | `9.0` | Selects the runtime .NET SDK release line. |
| `BLENDER_SERIES` | `4.5` | Selects the Blender release line. |
| `OS_BASE` | `ltsc2019` | Selects the Windows base release. |
| `CHOCOLATEY_VERSION` | `1.4.0` | Selects the Windows Chocolatey bootstrap version. |

## Commands

Run a Unity package command:

```console
docker run --rm faulo/unity:latest unity empty-project test 2022.3.62f3
```

The public `unity` executable forwards its arguments to `composer exec unity-command --`. The image starts `unity-sidecar` by default; health checks use `unity-sidecar health`.

## Credentials

Unity credentials must be injected by Jenkins or the host environment, not written into a Unity Docker Compose definition. They can be provided at container startup with:

- `UNITY_CREDENTIALS_USR` and `UNITY_CREDENTIALS_PSW`
- `UNITY_CREDENTIALS_USR_FILE` and `UNITY_CREDENTIALS_PSW_FILE`

Optional email credentials use the equivalent `EMAIL_CREDENTIALS_USR`, `EMAIL_CREDENTIALS_PSW`, `EMAIL_CREDENTIALS_USR_FILE`, and `EMAIL_CREDENTIALS_PSW_FILE` variables.

When MCP mode is enabled, credentials can instead be configured or rotated in memory at runtime with the `configure_credentials` tool. Updates are atomic, affect subsequent worker executions including retained containers, and never return secret values.

## Sidecar and MCP

Set `UNITY_MCP=1` to expose the Unity MCP server over HTTP on port 8080. Persistent runtime state is kept in `/run/unity` on Linux and `C:\ProgramData\unity` on Windows. `UNITY_CALL_TIMEOUT` controls the maximum command duration in seconds and defaults to 86400.

The sidecar creates isolated worker containers through the mounted Docker daemon. Mount the daemon socket or named pipe and the Unity editor volume appropriate to the host platform.

## Volumes

Linux declares Unity editor, configuration, cache, and license locations below `/root`. Windows declares the corresponding editor and application-data locations. The editor volume is named `unity-binaries` by the repository test tooling.

## Repository layout

- `common/Unity/` contains the cross-platform native launcher and sidecar.
- `common/Unity.Tests/` contains daemon-free NUnit tests.
- `common/Unity.DaemonTests/` and `common/Unity.DaemonTests.Backend/` contain real-daemon integration tests.
- `linux/` and `windows/` contain their platform Dockerfiles and package manifests.
- `tests/` contains published-image Pester integration coverage.

## Development

Build and test the solution:

```console
dotnet test docker-unity.sln --configuration Release
```

Run daemon tests explicitly against the named local Docker contexts:

```console
$env:UNITY_DAEMON_TEST_REQUIRED_OS = 'linux'
dotnet test common/Unity.DaemonTests/Unity.DaemonTests.csproj --configuration Release
```

Candidate images must use the disposable namespace:

```console
docker --context garl build --file linux/Dockerfile --tag tmp/unity:latest .
docker --context dende build --file windows/Dockerfile --tag tmp/unity:latest .
docker --context garl image tag tmp/unity:latest tmp/unity:latest-linux
docker --context dende image tag tmp/unity:latest tmp/unity:latest-windows
docker --context dende image tag tmp/unity:latest tmp/unity:latest-windows-ltsc2019
pwsh ./.jenkins/Invoke-IntegrationTests.ps1 -Namespace tmp -Context garl
pwsh ./.jenkins/Invoke-IntegrationTests.ps1 -Namespace tmp -Context dende
```
