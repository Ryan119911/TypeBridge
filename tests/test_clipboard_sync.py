"""Regression checks against the daemon callbacks on a private X server."""
import importlib.util
import os
import pathlib
import sys
import unittest
from unittest.mock import patch

ROOT = pathlib.Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))


@unittest.skipUnless(sys.platform == 'linux' and os.environ.get('DISPLAY') == ':97',
                     'requires private Xvfb :97')
class ClipboardSyncTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        spec = importlib.util.spec_from_file_location('clipboard_daemon', ROOT / 'typebridge-ubuntu.py')
        cls.daemon = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(cls.daemon)

    def setUp(self):
        self.d = self.daemon
        self.d._clipboard_state.update(applied='', sent='', primed=False,
            image_applied=None, image_sent=None, suppress_image=False)
        self.d._seq['clip'] = 0
        self.lines = []
        self.broadcast = patch.object(self.d, 'broadcast', side_effect=lambda line: self.lines.append(line) or 1)
        self.broadcast.start()
        self.addCleanup(self.broadcast.stop)

    def text(self, value):
        self.d._clipboard_text_ready(None, value, self.d._seq['clip'])

    def test_first_copy_after_empty_startup_reaches_windows(self):
        self.text(None)  # startup selection is empty
        self.d._seq['clip'] += 1
        self.text('first user copy')
        self.assertEqual(len(self.lines), 1, 'first real copy was swallowed as startup baseline')

    def test_first_image_copy_before_startup_read_finishes_reaches_windows(self):
        self.d._seq['clip'] = 1
        pix = self.d.GdkPixbuf.Pixbuf.new(self.d.GdkPixbuf.Colorspace.RGB, True, 8, 3, 2)
        pix.fill(0x234567ff)
        with patch.object(self.d.threading, 'Thread') as worker:
            self.d._clipboard_image_seen(pix)
            self.assertTrue(worker.called, 'a real image copy was mistaken for startup baseline')

    def test_recopy_old_remote_text_after_local_change_reaches_windows(self):
        self.text('startup')
        self.d.set_clipboard('remote A')
        self.text('remote A')  # echo
        self.text('local B')
        self.text('remote A')  # new user copy, no longer an echo
        self.assertEqual(len(self.lines), 2, 'old remote content was permanently suppressed')

    def test_image_after_text_does_not_consume_stale_echo_flag(self):
        self.text('startup')
        pix = self.d.GdkPixbuf.Pixbuf.new(self.d.GdkPixbuf.Colorspace.RGB, True, 8, 3, 2)
        pix.fill(0x123456ff)
        _, png = pix.save_to_bufferv('png', [], [])
        self.d.set_clipboard_image(png)
        self.text('new local text')  # replaces remote image before its async read returns
        pix.fill(0xff6543ff)
        with patch.object(self.d.threading, 'Thread') as worker:
            self.d._clipboard_image_seen(pix)
            self.assertTrue(worker.called, 'new image was swallowed by a stale remote echo flag')

    def test_recopy_image_after_remote_text_reaches_windows(self):
        self.text('startup')
        pix = self.d.GdkPixbuf.Pixbuf.new(self.d.GdkPixbuf.Colorspace.RGB, True, 8, 3, 2)
        pix.fill(0x654321ff)
        with patch.object(self.d.threading, 'Thread') as worker:
            self.d._clipboard_image_seen(pix)
            self.d.set_clipboard('remote text')
            self.text('remote text')
            self.d._clipboard_image_seen(pix)
            self.assertEqual(worker.call_count, 2, 'previous image incorrectly remained the current snapshot')


if __name__ == '__main__':
    unittest.main()
