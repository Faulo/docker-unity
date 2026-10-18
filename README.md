# Unity builds, tests, and MCP for Linux and Windows

[![Docker](https://github.com/Faulo/docker-unity/actions/workflows/docker.yml/badge.svg?branch=main)](https://github.com/Faulo/docker-unity/actions/workflows/docker.yml) [![Docker image updated](https://img.shields.io/docker/last-updated/faulo/unity/latest)](https://hub.docker.com/r/faulo/unity/tags?name=latest) [![License: MIT](https://img.shields.io/github/license/Faulo/docker-unity)](LICENSE) [![Platforms: Linux and Windows amd64](https://img.shields.io/badge/platforms-Linux%20%7C%20Windows%20amd64-0078D4)](#available-images)

[`faulo/unity`](https://hub.docker.com/r/faulo/unity) provides a container
environment for Unity builds and automated tests on Linux and Windows. It
combines Unity Hub, the [`slothsoft/unity`](https://github.com/Faulo/slothsoft-unity)
command tools, and a native sidecar with an optional Model Context Protocol
(MCP) server.

Run Unity commands directly in a build pipeline, or connect an MCP client to
discover projects, run tests, execute editor methods, and build WebGL previews.
Unity editors are installed on demand and can be retained in a shared volume.

[Docker Hub](https://hub.docker.com/r/faulo/unity) ·
[Image tags](https://hub.docker.com/r/faulo/unity/tags) ·
[Report an issue](https://github.com/Faulo/docker-unity/issues)

## Available images

| Tag | Platform | Base |
| --- | --- | --- |
| `latest` | Linux amd64 and Windows amd64 | Selects the matching platform |
| `latest-linux`, `latest-linux-amd64` | Linux amd64 | Debian Trixie Slim |
| `latest-windows`, `latest-windows-ltsc2019` | Windows amd64 | Full Windows LTSC 2019 through `faulo/choco:windows-ltsc2019` |

Linux images require an amd64 Linux container engine. Windows images require a
compatible Windows container engine and isolation mode. The Windows variant
uses the full Windows base image to provide the desktop APIs Unity needs.

Tags are rebuilt on a monthly schedule and when changes are published. Use an
image digest when a pipeline needs a fixed image revision. The configured tag
families are defined in [`docker.json`](docker.json).

## Quick start

Inspect the available commands:

```console
docker run --rm faulo/unity:latest unity --help
```

Run the test suite for an empty project with a specific Unity editor:

```console
docker run --rm --env UNITY_CREDENTIALS_USR --env UNITY_CREDENTIALS_PSW --env EMAIL_CREDENTIALS_USR --env EMAIL_CREDENTIALS_PSW --mount type=volume,source=unity-editors,target=/root/Unity faulo/unity:latest-linux unity empty-project test 2022.3.62f3
```

Set the credential variables in your shell or CI environment first. The first
run may download the editor and take substantially longer than later runs.
Unity authentication and licensing requirements still apply.

The example above uses Linux paths. On Windows, select
`faulo/unity:latest-windows` and use
`target=c:/program files/unity/hub/editor` for the editor volume, quoting the
complete `--mount` value because it contains spaces.

The public `unity` executable forwards arguments to
`composer exec unity-command --` and returns the command's exit status.
The image has no entrypoint, so commands such as `php`, `dotnet`, or
`blender` can also replace the default command.

## Included tools

| Tools | Purpose |
| --- | --- |
| Unity Hub and `slothsoft/unity` | Editor installation, Unity commands, builds, and tests |
| Native `unity` launcher and `unity-sidecar` | Command supervision, health checks, and MCP |
| Git and Git LFS | Source control and large assets |
| PHP 8.4 and Composer | Unity command backend, including IMAP support |
| .NET 9 SDK and DocFX | Managed builds and documentation |
| Blender 4.5 and FFmpeg | Asset processing |
| Python, Nano, and Zip | Scripting and utility commands |
| PowerShell on Windows | Windows automation |

Package declarations are maintained in
[`linux/unity.packages`](linux/unity.packages) and
[`windows/unity.nuspec`](windows/unity.nuspec), with additional setup in each
Dockerfile. Node.js, npm, Butler, and SteamCMD are not included.

## Run the MCP sidecar

The default command is `unity-sidecar`. Set `UNITY_MCP=1` to enable its HTTP
MCP endpoint at `/mcp` on container port 8080. The sidecar uses the mounted
Docker engine to inspect projects and create isolated worker containers from
its own image.

For a Linux container engine, save this as `compose.yaml`:

```yaml
services:
  unity:
    image: faulo/unity:latest-linux
    environment:
      UNITY_MCP: "1"
    ports:
      - "127.0.0.1:3310:8080"
    volumes:
      - /var/run/docker.sock:/var/run/docker.sock
      - unity-editors:/root/Unity
      - unity-config:/root/.config/unity3d
      - unity-hub:/root/.config/unityhub
      - unity-cache:/root/.cache/Unity
      - unity-licenses:/root/.local/share/unity3d

volumes:
  unity-editors:
  unity-config:
  unity-hub:
  unity-cache:
  unity-licenses:
```

For a Windows container engine, use this configuration instead:

```yaml
services:
  unity:
    image: faulo/unity:latest-windows
    environment:
      UNITY_MCP: "1"
    ports:
      - "127.0.0.1:3310:8080"
    volumes:
      - type: npipe
        source: '\\.\pipe\docker_engine'
        target: '\\.\pipe\docker_engine'
      - unity-editors:c:/program files/unity/hub/editor
      - unity-config:c:/users/containeradministrator/appdata/roaming/unity
      - unity-hub:c:/users/containeradministrator/appdata/roaming/unityhub
      - unity-cache:c:/users/containeradministrator/appdata/local/unity
      - unity-licenses:c:/programdata/unity

volumes:
  unity-editors:
  unity-config:
  unity-hub:
  unity-cache:
  unity-licenses:
```

Start the service and check its health:

```console
docker compose up -d
docker compose exec unity unity-sidecar health
docker compose logs --follow unity
```

Connect an HTTP MCP client to `http://127.0.0.1:3310/mcp`. Give the client a
timeout long enough for editor downloads and cold imports; the server sends
progress notifications during long operations. Cold imports can take
30–60 minutes.

The server accepts loopback host names and rejects non-loopback browser
origins. Keep the endpoint on a trusted local interface. Docker socket or
named-pipe access gives the sidecar control over that Docker engine.

Do not override the container hostname: the sidecar uses it to identify its
own container. Supply project paths as absolute paths visible to the Docker
daemon; a remote engine needs the path on its host. Workers mount the project's
`Assets`, `Packages`, and `ProjectSettings` directories and inherit the
sidecar's editor, configuration, cache, and license mounts.

### MCP tools

| Tool | Purpose |
| --- | --- |
| `get_project_info` | Read project metadata and package configuration without launching Unity |
| `run_tests` | Run Unity Test Runner modes and return test results |
| `execute_method` | Run a fully qualified static editor method with optional arguments |
| `build_and_serve_webgl` | Build WebGL and serve the result from the HTTP listener |

## Credentials and configuration

Direct commands and the sidecar accept these environment variables:

| Variable | Default | Purpose |
| --- | --- | --- |
| `UNITY_CREDENTIALS_USR`, `UNITY_CREDENTIALS_PSW` | Unset | Unity account credentials |
| `EMAIL_CREDENTIALS_USR`, `EMAIL_CREDENTIALS_PSW` | Unset | Mailbox credentials when Unity sends an email two-factor code |
| `UNITY_MCP` | `0` | Set to `1` to enable the MCP server |
| `UNITY_CALL_TIMEOUT` | `86400` | Maximum command duration in seconds |
| `UNITY_NO_GRAPHICS` | `1` | Configure headless Unity execution |
| `UNITY_ACCELERATOR_ENDPOINT` | Empty | Optional Unity Accelerator endpoint |
| `UNITY_LOGGING` | `stdin stdout stderr` | Command logging streams |

Each credential variable also supports a `_FILE` suffix, such as
`UNITY_CREDENTIALS_PSW_FILE`, to read a mounted file or Docker secret.
Supply credentials through your environment or secret store rather than
committing their values to a Compose file.

For shared MCP use, clients can send credentials with every HTTP request:

| Request header | Container fallback |
| --- | --- |
| `X-Unity-Credentials-Usr` | `UNITY_CREDENTIALS_USR` |
| `X-Unity-Credentials-Psw` | `UNITY_CREDENTIALS_PSW` |
| `X-Email-Credentials-Usr` | `EMAIL_CREDENTIALS_USR` |
| `X-Email-Credentials-Psw` | `EMAIL_CREDENTIALS_PSW` |

Each supplied header overrides its corresponding fallback for that request.
Request credentials are passed only to that tool execution and are not retained
in shared controller state, worker configuration, labels, logs, or tool results.
Missing values are allowed through so Unity can report its authentication error.

## Persistent storage

The Compose examples retain the image's declared editor, configuration, cache,
and license directories. The sidecar shares these mounts with its workers, so
editors can be reused across requests and container recreation.

Runtime supervision state lives at `/run/unity` on Linux and
`C:\ProgramData\unity` on Windows. The built-in Docker health check runs
`unity-sidecar health` every 60 seconds, with a 10-second timeout, 30-second
start period, and three retries.

## Development and tests

The repository contains daemon-free NUnit tests in
[`common/Unity.Tests`](common/Unity.Tests), real Docker-engine tests in
[`common/Unity.DaemonTests`](common/Unity.DaemonTests), and Pester image
integration tests in [`tests`](tests).

With the .NET 9 SDK installed, run the daemon-free tests:

```console
dotnet test common/Unity.Tests/Unity.Tests.csproj --configuration Release --logger junit
```

Integration coverage exercises installed tools, SDK compilation, Unity editor
commands, project discovery, argument and exit-status forwarding, concurrent
request credentials, worker reuse and mount isolation, progress notifications,
and WebGL HTTP behavior. Real-engine tests require a matching Linux or Windows
Docker engine and can create disposable containers, volumes, and images.

Both Dockerfiles use the repository root as their build context. Build a local
candidate with an engine matching the selected platform:

```console
docker build --file linux/Dockerfile --tag tmp/unity:latest .
```

```console
docker build --file windows/Dockerfile --tag tmp/unity:latest .
```

### Build arguments

| Argument | Default | Applies to |
| --- | --- | --- |
| `UNITY_TIMEOUT` | `14400` | Composer child-process timeout in seconds, both images |
| `DOTNET_VERSION` | `9.0` | Runtime SDK release line, both images |
| `BLENDER_SERIES` | `4.5` | Blender release line, both images |
| `OS_BASE` | `ltsc2019` | Windows base release |

The shared [Docker publication workflow](https://github.com/Faulo/workflows-docker)
uses [`docker.json`](docker.json) to define native builds, platform tags, and
Docker Hub metadata.

## Support and contributions

Report bugs and feature requests through
[GitHub issues](https://github.com/Faulo/docker-unity/issues). Include the image
tag or digest, container OS, Docker version, isolation mode where relevant, and
a minimal reproduction with credentials removed.

Contributions are welcome. Add tests for behavior changes and keep shared
launcher code in `common/`, with platform-specific packaging in `linux/` or
`windows/`.

## License

The repository's code and documentation are licensed under the
[MIT License](LICENSE). Unity Hub, Unity editors, and the other bundled tools
retain their respective licenses; the repository license does not grant a
Unity subscription or editor license.
