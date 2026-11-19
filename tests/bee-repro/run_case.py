"""Run inside a disposable Linux container; never write to the source checkout."""
import argparse
import hashlib
import json
import os
import re
import shutil
import signal
import subprocess
import threading
import time
from pathlib import Path


def processes():
    result = []
    for entry in Path('/proc').glob('[0-9]*'):
        try:
            name = (entry / 'comm').read_text().strip()
            executable_name = name
            if name in ('bee_backend.rea', 'bee_backend.uni') or name.startswith('ld-linux'):
                arguments = (entry / 'cmdline').read_bytes().split(b'\0')
                if any(argument.endswith((b'/bee_backend.real', b'/bee_backend.unity-original')) for argument in arguments[:6]):
                    name = 'bee_backend'
            if name not in ('Unity', 'bee_backend'):
                continue
            stat = (entry / 'stat').read_text().split(') ', 1)[1].split()
            threads = []
            for task in (entry / 'task').iterdir():
                threads.append({'tid': int(task.name), 'wchan': (task / 'wchan').read_text(),
                                'children': (task / 'children').read_text().strip()})
            result.append({'pid': int(entry.name), 'name': name, 'ppid': int(stat[1]),
                           'executableName': executable_name,
                           'pgid': int(stat[2]), 'startTicks': stat[19],
                           'cpuTicks': [stat[11], stat[12]],
                           'fds': {str(fd): os.readlink(entry / 'fd' / str(fd)) for fd in range(3)},
                           'command': redact((entry / 'cmdline').read_bytes().replace(b'\0', b' ').decode(errors='replace')),
                           'threads': threads})
        except (OSError, ValueError):
            continue
    return result


def redact(value):
    for key, secret in os.environ.items():
        if 'CREDENTIALS' in key and secret:
            value = value.replace(secret, '[redacted]')
    return re.sub(r'(?i)(Serial number[^:\r\n]*:|Serial:)[^\r\n]*', r'\1 [redacted]', value)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--version')
    parser.add_argument('--revision')
    parser.add_argument('--launch', choices=('native', 'composer'), default='native')
    parser.add_argument('--timeout', type=int, default=1800)
    parser.add_argument('--stall-seconds', type=int, default=120)
    parser.add_argument('--cancel-at-bee', choices=('launcher', 'editor'))
    args = parser.parse_args()
    evidence = Path('/results')
    project = Path('/work/project')
    project.mkdir(parents=True)
    for name in ('Assets', 'Packages', 'ProjectSettings'):
        shutil.copytree(Path('/source') / name, project / name, symlinks=False)
    original = (project / 'ProjectSettings/ProjectVersion.txt').read_text()
    if args.version:
        if not args.revision:
            raise ValueError('An editor override requires its official revision')
        (project / 'ProjectSettings/ProjectVersion.txt').write_text(
            f'm_EditorVersion: {args.version}\n'
            f'm_EditorVersionWithRevision: {args.version} ({args.revision})\n')
    if Path('/license-seed').exists():
        shutil.copytree('/license-seed', '/root/.local/share/unity3d', dirs_exist_ok=True)
    command = ['unity'] if args.launch == 'native' else ['composer', 'exec', 'unity-command', '--']
    command += ['build', '--junit', '-', str(project), '/work/output', 'windows']
    metadata = {'command': command, 'originalVersionFile': original,
                'versionOverride': args.version, 'coldLibrary': not (project / 'Library').exists(),
                'glibcTunables': os.environ.get('GLIBC_TUNABLES', ''),
                'os': Path('/etc/os-release').read_text(), 'kernel': os.uname().release,
                'glibc': subprocess.check_output(['ldd', '--version'], text=True).splitlines()[0]}
    start = time.monotonic()
    child = subprocess.Popen(command, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                             text=True, errors='replace', start_new_session=True)
    def pump():
        with (evidence / 'build.log').open('w') as output:
            for line in child.stdout:
                output.write(redact(line))
                output.flush()
    reader = threading.Thread(target=pump, daemon=True)
    reader.start()
    last_signature = None
    stable_since = None
    verdict = None
    cancellation = None
    while child.poll() is None:
        elapsed = time.monotonic() - start
        snapshot = processes()
        if args.cancel_at_bee and cancellation is None:
            bees_at_cancel = [p for p in snapshot if p['name'] == 'bee_backend']
            editors = [p for p in snapshot if p['name'] == 'Unity']
            if bees_at_cancel and editors:
                target = child.pid if args.cancel_at_bee == 'launcher' else editors[0]['pid']
                cancellation = {'target': args.cancel_at_bee, 'pid': target,
                                'elapsedSeconds': round(elapsed, 2), 'signal': 'SIGTERM',
                                'processesBeforeSignal': snapshot}
                os.kill(target, signal.SIGTERM)
                time.sleep(20)
                cancellation['processesAfter20Seconds'] = processes()
                cancellation['launcherExitCode'] = child.poll()
                verdict = 'cancelled_cleanly' if not cancellation['processesAfter20Seconds'] else 'cancelled_with_survivors'
                break
        profiles = []
        for path in (project / 'Library/Bee').glob('backend_profiler*.traceevents'):
            data = path.read_bytes()
            profiles.append({'name': path.name, 'size': len(data), 'mtime': path.stat().st_mtime,
                             'sha256': hashlib.sha256(data).hexdigest(),
                             'tail': data[-1500:].decode(errors='replace')})
        bees = [p for p in snapshot if p['name'] == 'bee_backend' and '--stdin-canary' in p['command']]
        idle = bool(bees) and all(len(p['threads']) == 2 and
            not any(t['children'] for t in p['threads']) and
            any('pipe_read' in t['wchan'] for t in p['threads']) and
            any('futex' in t['wchan'] for t in p['threads']) for p in bees)
        newest = max(profiles, key=lambda p: p['mtime'], default={})
        signature = ([(p['pid'], p['startTicks']) for p in bees],
                     [(p['name'], p['sha256']) for p in profiles])
        if idle and 'ProfilerWriteOutput' in newest.get('tail', ''):
            if signature != last_signature:
                stable_since = time.monotonic()
            if stable_since is not None and time.monotonic() - stable_since >= args.stall_seconds:
                verdict = 'bee_stall'
        else:
            stable_since = None
        last_signature = signature
        observation = {'elapsedSeconds': round(elapsed), 'processes': snapshot, 'profiles': profiles,
                       'memory': Path('/proc/meminfo').read_text(),
                       'stableSeconds': round(time.monotonic() - stable_since) if stable_since else 0}
        with (evidence / 'observations.jsonl').open('a') as output:
            output.write(json.dumps(observation) + '\n')
        if verdict or elapsed >= args.timeout:
            verdict = verdict or 'timeout_unclassified'
            break
        time.sleep(0.1 if args.cancel_at_bee else 10)
    metadata.update({'verdict': verdict, 'elapsedSeconds': round(time.monotonic() - start),
                     'cancellationRequested': args.cancel_at_bee,
                     'cancellation': cancellation,
                     'exitCodeBeforeCleanup': child.poll(), 'processesBeforeCleanup': processes()})
    for path in (project / 'Library/Bee').glob('backend_profiler*.traceevents'):
        shutil.copy2(path, evidence / path.name)
    players = [p for p in Path('/work/output').rglob('*.exe') if 'UnityCrashHandler' not in p.name]
    metadata['players'] = [{'path': str(p), 'bytes': p.stat().st_size} for p in players]
    if verdict is None:
        metadata['verdict'] = 'passed' if child.returncode == 0 and players else 'build_failed'
        if args.cancel_at_bee and cancellation is None:
            metadata['verdict'] = 'cancellation_not_triggered'
    # PID namespace contains only this case. Container removal is the final cleanup guard.
    if child.poll() is None:
        child.terminate()
        try:
            child.wait(timeout=15)
        except subprocess.TimeoutExpired:
            child.kill()
    metadata['processesAfterLauncherTermination'] = processes()
    (evidence / 'result.json').write_text(json.dumps(metadata, indent=2))
    print(json.dumps({'verdict': metadata['verdict'], 'elapsedSeconds': metadata['elapsedSeconds']}), flush=True)


if __name__ == '__main__':
    main()
