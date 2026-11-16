"""Local Linux Docker reproduction matrix. Source checkouts are mounted read-only."""
import argparse
import hashlib
import json
import subprocess
import time
import uuid
from pathlib import Path
from urllib.request import urlopen

PROJECTS = ['BattleOfTheGods', 'BrieYourself', 'ChartRunner', 'Communiganda',
            'FindingHome', 'IAmSusi', 'KeepEverybodyHappy', 'NanoMixture',
            'NuttinToLose', 'Orbital', 'ReverseSlender', 'SheepThrills',
            'SonarUndDochSoFern', 'SpaceCape']


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--projects-root', type=Path, default=Path.home() / 'Desktop/Unity')
    parser.add_argument('--results', type=Path, default=Path('.reports/bee-repro'))
    parser.add_argument('--projects', nargs='+', default=PROJECTS)
    parser.add_argument('--image', default='faulo/unity:latest')
    parser.add_argument('--editor-volume', default='unity-binaries')
    parser.add_argument('--bee-wrapper', type=Path, help='Override Bee in a private editor with a .real backup')
    parser.add_argument('--compat-volume', help='Mount an isolated runtime at /compat (read-only)')
    parser.add_argument('--execstack-compat', action='store_true')
    parser.add_argument('--wsl-paths', action='store_true', help='Use /mnt/<drive> bind sources on a native WSL daemon')
    parser.add_argument('--version')
    parser.add_argument('--launch', choices=('native', 'composer'), default='native')
    parser.add_argument('--timeout', type=int, default=1800)
    parser.add_argument('--stall-seconds', type=int, default=120)
    parser.add_argument('--cancel-at-bee', choices=('launcher', 'editor'))
    args = parser.parse_args()
    def mount_path(path):
        if args.wsl_paths:
            if not path.drive or len(path.drive) != 2:
                raise ValueError('WSL path conversion requires a Windows drive path')
            return '/mnt/' + path.drive[0].lower() + path.as_posix()[2:]
        return path.as_posix()
    def docker(*command, **kwargs):
        # A transient Desktop transport error must not abort an otherwise healthy build.
        attempts = 6
        kwargs.setdefault('stderr', subprocess.STDOUT)
        for attempt in range(attempts):
            try:
                return subprocess.check_output(['docker', '--context', 'linux', *command],
                                               text=True, timeout=120, **kwargs).strip()
            except (subprocess.CalledProcessError, subprocess.TimeoutExpired) as error:
                output = getattr(error, 'output', '') or ''
                if isinstance(output, bytes):
                    output = output.decode(errors='replace')
                if command[0] == 'run' and 'already in use' in output:
                    # The preceding request may have succeeded before its response was lost.
                    owned_name = command[command.index('--name') + 1]
                    return docker('inspect', '--format', '{{.Id}}', owned_name)
                if command[0] == 'rm' and 'No such container' in output:
                    return ''
                if isinstance(error, subprocess.CalledProcessError) and not any(
                    text in output for text in ('Hyper-V socket', 'failed to connect', 'must be absolute')
                ):
                    raise
                if attempt == attempts - 1:
                    raise
                time.sleep(2)
    image = json.loads(docker('image', 'inspect', args.image))[0]
    release = None
    if args.version:
        url = 'https://services.api.unity.com/unity/editor/release/v1/releases?limit=1&version=' + args.version
        with urlopen(url, timeout=30) as response:
            release = json.load(response)['results'][0]
        if release['version'] != args.version:
            raise ValueError('Release API did not return the exact requested version')
    for name in args.projects:
        source = (args.projects_root / ('GameJam.' + name)).resolve()
        version = args.version or (source / 'ProjectSettings/ProjectVersion.txt').read_text().splitlines()[0].split(': ')[1]
        destination = (args.results / f'{name}-{args.launch}-{version}').resolve()
        if destination.exists():
            raise RuntimeError(f'Refusing to overwrite evidence: {destination}')
        destination.mkdir(parents=True)
        provenance = source / '.repro-source.json'
        if provenance.exists():
            source_provenance = json.loads(provenance.read_text())
            revision, status = source_provenance['commit'], source_provenance['status']
        else:
            source_provenance = None
            revision = subprocess.check_output(['git', '-C', str(source), 'rev-parse', 'HEAD'], text=True).strip()
            status = subprocess.check_output(['git', '-C', str(source), 'status', '--porcelain'], text=True)
        (destination / 'source.json').write_text(json.dumps({
            'path': str(source), 'commit': revision, 'status': status,
            'sourceKind': 'git-export' if source_provenance else ('checkout' if (source / '.git').exists() else 'fixture'),
            'exportProvenance': source_provenance, 'execstackCompatibility': args.execstack_compat,
            'overrideRelease': release,
            'imageId': image['Id'], 'repoDigests': image.get('RepoDigests', []),
            'noGraphics': False, 'editorVolume': args.editor_volume}, indent=2))
        if args.bee_wrapper:
            wrapper = args.bee_wrapper.resolve()
            (destination / 'wrapper.json').write_text(json.dumps({
                'path': str(wrapper), 'sha256': hashlib.sha256(wrapper.read_bytes()).hexdigest(),
                'content': wrapper.read_text(), 'compatVolume': args.compat_volume}, indent=2))
        container = 'bee-repro-' + uuid.uuid4().hex[:12]
        command = ['run', '--detach', '--no-healthcheck', '--name', container]
        for key in ('UNITY_CREDENTIALS_USR', 'UNITY_CREDENTIALS_PSW',
                    'EMAIL_CREDENTIALS_USR', 'EMAIL_CREDENTIALS_PSW'):
            command += ['--env', key]
        command += ['--env', f'UNITY_CALL_TIMEOUT={args.timeout}', '--env', 'UNITY_NO_GRAPHICS=0']
        if args.execstack_compat:
            command += ['--env', 'GLIBC_TUNABLES=glibc.rtld.execstack=2']
        for mount in (f'type=bind,source={mount_path(source)},target=/source,readonly',
                      f'type=bind,source={mount_path(Path(__file__).parent.resolve())},target=/repro,readonly',
                      f'type=bind,source={mount_path(destination)},target=/results',
                      f'type=volume,source={args.editor_volume},target=/root/Unity,readonly',
                      'type=volume,source=unity-license,target=/license-seed,readonly'):
            command += ['--mount', mount]
        if args.bee_wrapper:
            command += ['--mount', f'type=bind,source={mount_path(wrapper)},target=/root/Unity/Hub/Editor/{version}/Editor/Data/bee_backend,readonly']
        if args.compat_volume:
            command += ['--mount', f'type=volume,source={args.compat_volume},target=/compat,readonly']
        command += ['--workdir', '/work', image['Id'], 'python3', '/repro/run_case.py',
                    '--launch', args.launch, '--timeout', str(args.timeout),
                    '--stall-seconds', str(args.stall_seconds)]
        if args.version:
            command += ['--version', args.version, '--revision', release['shortRevision']]
        if args.cancel_at_bee:
            command += ['--cancel-at-bee', args.cancel_at_bee]
        print(f'START {name} {version} {args.launch}', flush=True)
        try:
            docker(*command)
            deadline = time.monotonic() + args.timeout + 600
            while docker('inspect', '--format', '{{.State.Running}}', container) == 'true':
                if time.monotonic() >= deadline:
                    raise TimeoutError(f'Outer deadline exceeded: {container}')
                time.sleep(10)
            (destination / 'container-state.json').write_text(
                docker('inspect', '--format', '{{json .State}}', container))
            (destination / 'runner.log').write_text(docker('logs', container, stderr=subprocess.STDOUT))
            result = json.loads((destination / 'result.json').read_text())
            print(f"RESULT {name}: {result['verdict']} ({result['elapsedSeconds']}s)", flush=True)
        except Exception as error:
            (destination / 'infrastructure-error.txt').write_text(str(error))
            raise
        finally:
            docker('rm', '--force', '--volumes', container)


if __name__ == '__main__':
    main()
