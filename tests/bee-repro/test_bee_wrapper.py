"""Run on Linux: verify that the workaround preserves Bee's command contract."""
import json
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path


class BeeWrapperTest(unittest.TestCase):
    def test_preserves_arguments_pid_and_exit_status(self):
        with tempfile.TemporaryDirectory(prefix='bee wrapper ') as directory:
            wrapper = Path(directory) / 'bee_backend'
            shutil.copyfile(Path(__file__).with_name('bee_without_canary.sh'), wrapper)
            wrapper.chmod(0o755)
            real = wrapper.with_name('bee_backend.real')
            real.write_text('#!/usr/bin/python3\nimport json, os, sys\n'
                            'print(json.dumps([os.getpid(), sys.argv[1:]]))\n'
                            'sys.exit(17)\n')
            real.chmod(0o755)
            cases = [[], ['--stdin-canary'], ['--stdin-canary', '--stdin-canary'],
                     ['', 'path with spaces', '"quoted"', '$literal', '*'],
                     ['first', '--stdin-canary', '', '--stdin-canary=1', 'last'],
                     ['--profile=Library/Bee/profile.traceevents', '--stdin-canary',
                      '--dagfile=Library/Bee/Player.dag', '--continue-on-failure', 'Player']]
            for arguments in cases:
                with self.subTest(arguments=arguments):
                    child = subprocess.Popen([str(wrapper), *arguments], stdout=subprocess.PIPE, text=True)
                    stdout, _ = child.communicate(timeout=5)
                    pid, forwarded = json.loads(stdout)
                    self.assertEqual(pid, child.pid)
                    self.assertEqual(child.returncode, 17)
                    self.assertEqual(forwarded, [a for a in arguments if a != '--stdin-canary'])


if __name__ == '__main__':
    unittest.main()
