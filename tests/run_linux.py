"""Run daemon integration checks in an isolated X server."""
import os
import pathlib
import shutil
import subprocess
import sys
import time

root = pathlib.Path(__file__).resolve().parents[1]
xvfb = os.environ.get('TYPEBRIDGE_XVFB') or shutil.which('Xvfb')
if not xvfb:
    raise SystemExit('Set TYPEBRIDGE_XVFB to an Xvfb executable')
env = dict(os.environ, DISPLAY=':97', XAUTHORITY='/nonexistent',
           DBUS_SESSION_BUS_ADDRESS='unix:path=/nonexistent')
with open(root / 'test-linux-xvfb.log', 'w') as log:
    server = subprocess.Popen([xvfb, ':97', '-screen', '0', '1280x800x24', '-nolisten', 'tcp', '-ac'],
                              stdout=log, stderr=log)
    try:
        for _ in range(50):
            if server.poll() is not None:
                raise RuntimeError('Private X server failed')
            if subprocess.run(['xdpyinfo'], env=env, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL).returncode == 0:
                break
            time.sleep(.1)
        result = subprocess.run([sys.executable, '-m', 'unittest', 'discover', '-s', 'tests', '-v'], cwd=root, env=env)
        sys.exit(result.returncode)
    finally:
        server.terminate()
        server.wait(timeout=5)
