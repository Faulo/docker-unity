# Unity Docker image

Cross-platform Unity build and test image for Linux and Windows. Published images are available as `faulo/unity`.

## Included tools

Both variants include Unity Hub, the `unity` command stack, Git and Git LFS, PHP 8.4 and Composer, .NET SDK and DocFX, Python, FFmpeg, Blender, Nano, and Zip. Windows also includes PowerShell. The Linux base packages are declared in [`linux/unity.packages`](linux/unity.packages); Windows dependencies are declared in [`windows/unity.nuspec`](windows/unity.nuspec). Windows installs the current official PECL IMAP build because PHP 8.4 no longer bundles it.

Node.js, npm, Butler, and SteamCMD are intentionally not included.

## Image variants

- `faulo/unity:latest` is the primary multi-platform image.
- `faulo/unity:latest-linux-amd64` targets Linux amd64.
- `faulo/unity:latest-windows-ltsc2019` targets Windows Server 2019 specifically.

The target-specific tags contain only the selected target and its attestations. Integration tests cover `latest` and the matching target-specific tag on each host. Linux hosts require an amd64 Docker daemon. Windows images require a compatible Windows Server 2019 daemon.

GitHub builds the Windows LTSC 2019 target on `windows-2022` with Hyper-V isolation. The runner is explicit so a `windows-latest` host upgrade cannot silently change the build environment; the Windows Server 2025 runner failed to import the combined dependency layer during this migration.

The former `latest-linux` and `latest-windows` tags are no longer maintained. Use `latest-linux-amd64` and `latest-windows-ltsc2019` instead. Intermediate platform/family tags are requested in [workflows-docker #2](https://github.com/Faulo/workflows-docker/issues/2); historical tags are not deleted by this migration.

Windows derives from `faulo/choco:windows-${OS_BASE}` to retain Unity's full desktop API surface. The shared base supplies Chocolatey, Visual C++ prerequisites, and shell/installer compatibility fixes. The existing package dependencies install through `choco-install`, including the selected .NET SDK and Blender release line. Unity Hub/editor extraction and application-specific setup remain in this project.

Windows installs the .NET Framework 4.7.1 Developer Pack through its normal Chocolatey dependency for Unity-generated projects. Temporary archive tooling is declared separately in [`windows/unity-build.nuspec`](windows/unity-build.nuspec); its package and 7-Zip are removed after Unity Hub preparation. The SDK is checked by compiling and running a real console application in a fresh build layer and in the image integration suite.

Report ordinary Chocolatey installer stalls or failures to [Faulo/docker-choco](https://github.com/Faulo/docker-choco/issues) and link them from the downstream migration. Include the package ID/version, base tag/digest, host Windows build and isolation mode, exact command/nuspec, relevant complete logs, exit code or timeout, and a minimal fresh-container reproduction without credentials. Keep the affected migration blocked until the shared fix is available and validated; do not add downstream installer bypasses or compatibility patches.

## Build arguments

| Argument | Default | Purpose |
| --- | --- | --- |
| `UNITY_TIMEOUT` | `14400` | Configures Composer's child-process timeout in seconds. |
| `DOTNET_VERSION` | `9.0` | Selects the runtime .NET SDK release line. |
| `BLENDER_SERIES` | `4.5` | Selects the Blender release line. |
| `OS_BASE` | `ltsc2019` | Selects the Windows base release. |

## Commands

Run a Unity package command:

```console
docker run --rm faulo/unity:latest unity empty-project test 2022.3.62f3
```

The public `unity` executable forwards its arguments to `composer exec unity-command --`. The image starts `unity-sidecar` by default; health checks use `unity-sidecar health`.

## Credentials

For a single credential identity, the sidecar and direct `unity` commands accept these container environment variables as fallbacks:

- `UNITY_CREDENTIALS_USR` and `UNITY_CREDENTIALS_PSW`
- `UNITY_CREDENTIALS_USR_FILE` and `UNITY_CREDENTIALS_PSW_FILE`

Email credentials use the equivalent `EMAIL_CREDENTIALS_USR`, `EMAIL_CREDENTIALS_PSW`, `EMAIL_CREDENTIALS_USR_FILE`, and `EMAIL_CREDENTIALS_PSW_FILE` variables. They are needed when Unity sends its two-factor code by email. Values may come from the host environment, Jenkins, Docker Compose interpolation, or Docker secrets; avoid committing credentials to a Compose file.

For a shared MCP server, each client should send credentials with every HTTP request. The four request headers are `X-Unity-Credentials-Usr`, `X-Unity-Credentials-Psw`, `X-Email-Credentials-Usr`, and `X-Email-Credentials-Psw`. A supplied header overrides its corresponding container fallback for that request only. Missing values are allowed through so Unity can report its normal authentication error; the MCP server does not require complete pairs. Request header values are captured only for that tool execution and are not stored in shared controller state, retained worker configuration, labels, logs, or tool results.

Codex can map local environment variables to those headers without putting their values in `config.toml`:

```toml
[mcp_servers.compose-unity]
url = "http://127.0.0.1:3310/mcp"
tool_timeout_sec = 3600

[mcp_servers.compose-unity.env_http_headers]
X-Unity-Credentials-Usr = "UNITY_CREDENTIALS_USR"
X-Unity-Credentials-Psw = "UNITY_CREDENTIALS_PSW"
X-Email-Credentials-Usr = "EMAIL_CREDENTIALS_USR"
X-Email-Credentials-Psw = "EMAIL_CREDENTIALS_PSW"
```

Each Codex process reads the named variables from its own environment and sends the resulting headers, so multiple clients can use the same `compose-unity` endpoint with different identities. `env_vars` is not used here because it configures environment forwarding for local stdio MCP servers, not headers for an HTTP MCP server.

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
dotnet test docker-unity.sln --configuration Release --logger junit
```

Run daemon tests explicitly against the named local Docker contexts:

```console
$env:UNITY_DAEMON_TEST_REQUIRED_OS = 'linux'
dotnet test common/Unity.DaemonTests/Unity.DaemonTests.csproj --configuration Release --logger junit
```

Candidate images must use the disposable namespace:

```console
docker --context garl build --file linux/Dockerfile --tag tmp/unity:latest .
docker --context dende build --file windows/Dockerfile --tag tmp/unity:latest .
docker --context garl image tag tmp/unity:latest tmp/unity:latest-linux-amd64
docker --context dende image tag tmp/unity:latest tmp/unity:latest-windows-ltsc2019
pwsh ./.jenkins/Invoke-IntegrationTests.ps1 -Namespace tmp -Context garl
pwsh ./.jenkins/Invoke-IntegrationTests.ps1 -Namespace tmp -Context dende
pwsh ./.jenkins/Invoke-IntegrationTests.ps1 -Namespace tmp -Context garl -Variant latest-linux-amd64
pwsh ./.jenkins/Invoke-IntegrationTests.ps1 -Namespace tmp -Context dende -Variant latest-windows-ltsc2019
```

GitHub uses the shared [workflows-docker v3 caller](https://github.com/Faulo/workflows-docker/blob/v3/examples/docker.yml) and [`docker.json`](docker.json) for native builds and publication. Namespace and image resolve from the same tracked `.env` used by Jenkins. Docker Hub credentials must allow publication and deletion of run-specific staging tags. GitHub verifies published digests/platform metadata and staging-tag cleanup; project tests run in Jenkins.

Jenkins runs unit tests in the .NET 9 SDK container on a Linux agent and publishes each stage's reports with its native `junit` step.
The unit project writes `common/Unity.Tests/TestResults/TestResults.xml`; daemon stages write fresh reports below `.reports/daemon-<os>/` and select only their OS fixture so Jenkins receives no duplicate opposite-OS skips.
Separate Jenkins daemon stages run on Garl and Dende through [`.jenkins/Invoke-DaemonTests.ps1`](.jenkins/Invoke-DaemonTests.ps1). The script builds a disposable SDK runner with the Docker client, connects through the local `linux` or `windows` context, translates mounted agent workspaces to daemon-visible fixture paths, and requires coverage for the selected OS with `UNITY_DAEMON_TEST_REQUIRED_OS`. Linux uses host networking for the fixture's loopback port. Windows uses process isolation on LTSC 2019 hosts and Hyper-V on newer hosts, passed to fixture builds and controller containers without changing the daemon's global configuration.

Run the same daemon runner locally with `pwsh ./.jenkins/Invoke-DaemonTests.ps1 -Context linux` or `-Context windows`. `-RepositoryHostPath` supplies an explicit checkout path visible to the daemon when automatic Jenkins mount detection is unavailable; `-WindowsIsolation` overrides the compatible Windows isolation selection.

Image integration tests use the hosts and product variants configured in `.jenkins/pesterProject.properties`, plus each host's matching target-specific tag. Every invocation selects its Docker context explicitly.
