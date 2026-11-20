"""Exercise the daemon's real motion handler with deterministic X event ordering."""
import importlib.util
import os
import pathlib
import select
import subprocess
import sys
import types
import unittest
from unittest import mock

ROOT = pathlib.Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))


@unittest.skipUnless(sys.platform == 'linux' and os.environ.get('DISPLAY') == ':97',
                     'requires private Xvfb :97')
class MotionTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        spec = importlib.util.spec_from_file_location('typebridge_motion_test', ROOT / 'typebridge-ubuntu.py')
        cls.daemon = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(cls.daemon)

    def setUp(self):
        self.state = self.daemon._RWIN
        self.state.update(active=True, sw=1280, sh=800, last_x=100, last_y=400,
                          dx=0, dy=0, warp_target=None)

    def motion(self, x, y):
        self.daemon._on_x_motion(types.SimpleNamespace(x_root=x, y_root=y))

    def test_fast_move_reaches_edge_and_recenters(self):
        with mock.patch.object(self.daemon, '_warp') as warp:
            self.motion(0, 400)
            self.assertEqual(self.state['dx'], -100, 'a fast real stroke must be forwarded')
            warp.assert_called_once_with(640, 400)

    def test_queued_events_before_warp_do_not_create_false_motion(self):
        self.state.update(last_x=20)
        with mock.patch.object(self.daemon, '_warp'):
            self.motion(2, 400)  # queues a recenter
            self.motion(1, 400)  # already queued before that recenter
            self.motion(640, 400)  # the recenter acknowledgement
            self.motion(630, 400)  # first subsequent physical movement
        self.assertEqual(self.state['dx'], -28)

    def test_small_warp_must_not_be_forwarded(self):
        self.state.update(sw=120, sh=80, last_x=10, last_y=40)
        with mock.patch.object(self.daemon, '_warp'):
            self.motion(2, 40)
            self.motion(1, 40)
            self.motion(60, 40)
            self.motion(50, 40)
        self.assertEqual(self.state['dx'], -18, 'warp filtering must not depend on its distance')

    def test_center_without_warp_does_not_swallow_next_move(self):
        self.state.update(last_x=640, last_y=400)
        with mock.patch.object(self.daemon, '_warp') as warp:
            self.daemon._center_remote_pointer(640, 400)
            warp.assert_not_called()
            self.motion(630, 400)
        self.assertEqual(self.state['dx'], -10)

    def test_tick_drains_hotkey_already_buffered_by_xlib(self):
        daemon = self.daemon
        self.state.update(active=False)
        self.assertTrue(daemon.ensure_display())
        daemon._register_hotkey()
        try:
            subprocess.run(['xdotool', 'key', 'ctrl+alt+u'], check=True)
            # A synchronous X reply can consume the FD into Xlib's private queue.
            # GLib's FD watch will then never fire until some later X traffic.
            daemon._x11.XSync(daemon._DPY, 0)
            self.assertGreater(daemon._x11.XPending(daemon._DPY), 0)
            fd = daemon._x11.XConnectionNumber(daemon._DPY)
            self.assertEqual(select.select([fd], [], [], 0)[0], [])
            with mock.patch.object(daemon, '_enter') as enter:
                daemon._rwin_tick()
                enter.assert_called_once_with('热键')
        finally:
            daemon._unregister_hotkey()


if __name__ == '__main__':
    unittest.main()
