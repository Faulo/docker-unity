"""Exercise the installed native launcher with detached, TERM-resistant children."""
import json
import os
import signal
import subprocess
import tempfile
import time
from pathlib import Path


def alive(pid):
    try:
        return Path(f'/proc/{pid}/stat').read_text().split(') ', 1)[1].split()[0] != 'Z'
    except FileNotFoundError:
        return False


def wait_for(predicate, timeout=30):
    deadline = time.monotonic() + timeout
    while not predicate():
        if time.monotonic() > deadline:
            raise AssertionError('Timed out waiting for process fixture')
        time.sleep(0.05)


with tempfile.TemporaryDirectory() as directory:
    root = Path(directory)
    composer = root / 'composer'
    composer.write_text('''#!/usr/bin/python3
import os, signal, sys, time
from pathlib import Path
if '--version' in sys.argv:
    print('Composer version fixture')
    sys.exit(0)
child = os.fork()
if child == 0:
    os.setsid()
    signal.signal(signal.SIGTERM, signal.SIG_IGN)
    Path(os.environ['FIXTURE_CHILD']).write_text(str(os.getpid()))
    while True: time.sleep(1)
if os.environ.get('FIXTURE_ROOT_EXIT'):
    while not Path(os.environ['FIXTURE_CHILD']).exists(): time.sleep(0.01)
    sys.exit(0)
while True: time.sleep(1)
''')
    composer.chmod(0o755)
    state = root / 'state'
    env = dict(os.environ, PATH=directory + ':' + os.environ['PATH'],
               UNITY_CALL_TIMEOUT='0', UNITY_STATE_DIRECTORY=str(state), UNITY_MCP='0')
    other = subprocess.Popen(['unity', 'fixture'], env=dict(env, FIXTURE_CHILD=str(root / 'other')))
    target = subprocess.Popen(['unity', 'fixture'], env=dict(env, FIXTURE_CHILD=str(root / 'target')))
    try:
        wait_for(lambda: (root / 'other').exists() and (root / 'target').exists())
        other_pid = int((root / 'other').read_text())
        target_pid = int((root / 'target').read_text())
        target.send_signal(signal.SIGTERM)
        assert target.wait(timeout=25) == 130
        wait_for(lambda: not alive(target_pid), timeout=3)
        assert other.poll() is None and alive(other_pid), 'Cancellation killed another invocation'
        other.send_signal(signal.SIGTERM)
        assert other.wait(timeout=25) == 130
        wait_for(lambda: not alive(other_pid), timeout=3)
        print(json.dumps({'cancelled': target_pid, 'isolated': other_pid, 'result': 'passed'}))
        for mode, additions, expected in (
            ('timeout', {'UNITY_CALL_TIMEOUT': '1'}, 124),
            ('root-exit', {'FIXTURE_ROOT_EXIT': '1'}, 0),
        ):
            path = root / mode
            child = subprocess.Popen(['unity', 'fixture'], env=dict(env, FIXTURE_CHILD=str(path), **additions))
            try:
                wait_for(path.exists)
                assert child.wait(timeout=25) == expected
                pid = int(path.read_text())
                wait_for(lambda pid=pid: not alive(pid), timeout=3)
                print(json.dumps({'scenario': mode, 'result': 'passed'}))
            finally:
                if child.poll() is None:
                    child.kill()
                    child.wait()
                if path.exists() and alive(int(path.read_text())):
                    os.kill(int(path.read_text()), signal.SIGKILL)
        supervisor = subprocess.Popen(['unity-sidecar'], env=env)
        orphan = None
        try:
            wait_for(lambda: (state / 'ready.json').exists())
            path = root / 'orphan'
            orphan = subprocess.Popen(['unity', 'fixture'], env=dict(env, FIXTURE_CHILD=str(path)))
            wait_for(path.exists)
            wait_for(lambda: any((state / 'active').glob('*.json')))
            orphan.kill()
            orphan.wait()
            pid = int(path.read_text())
            wait_for(lambda: not alive(pid), timeout=25)
            print(json.dumps({'scenario': 'launcher-disappeared', 'result': 'passed'}))
        finally:
            supervisor.terminate()
            supervisor.wait(timeout=25)
            if orphan is not None and orphan.poll() is None:
                orphan.kill()
                orphan.wait()
            if (root / 'orphan').exists() and alive(int((root / 'orphan').read_text())):
                os.kill(int((root / 'orphan').read_text()), signal.SIGKILL)
    finally:
        for process in (target, other):
            if process.poll() is None:
                process.kill()
                process.wait()
        for name in ('target', 'other'):
            if (root / name).exists():
                pid = int((root / name).read_text())
                if alive(pid):
                    os.kill(pid, signal.SIGKILL)
