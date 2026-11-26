import ctypes as C
import json
import os
import pathlib
import select
import socket
import subprocess
import sys
import tempfile
import time
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))


def free_port():
    with socket.socket() as sock:
        sock.bind(('127.0.0.1', 0))
        return sock.getsockname()[1]


class Peer:
    def __init__(self, port):
        self.sock = socket.create_connection(('127.0.0.1', port), 3)
        self.buf = b''

    def send(self, line):
        self.sock.sendall((line + '\n').encode())

    def until(self, prefix, timeout=4):
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            while b'\n' in self.buf:
                line, self.buf = self.buf.split(b'\n', 1)
                line = line.decode()
                if line.startswith(prefix):
                    return line
            if select.select([self.sock], [], [], max(0, deadline - time.monotonic()))[0]:
                data = self.sock.recv(65536)
                if not data:
                    raise EOFError('control connection closed')
                self.buf += data
        raise TimeoutError('expected ' + prefix)

    def close(self):
        self.sock.close()


@unittest.skipUnless(sys.platform == 'linux' and os.environ.get('DISPLAY') == ':97', 'requires private Xvfb :97')
class DaemonTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temp = tempfile.TemporaryDirectory(prefix='typebridge-tests-')
        cls.directory = pathlib.Path(cls.temp.name)
        cls.port, cls.file_port = free_port(), free_port()
        config = {'bind': '127.0.0.1', 'port': cls.port, 'file_port': cls.file_port,
                  'file_dir': str(cls.directory / 'received'), 'outbox_dir': str(cls.directory / 'outbox'), 'edge_entry': False, 'hotkey': 'ctrl+alt+u'}
        (cls.directory / 'config.json').write_text(json.dumps(config))
        cls.log = open(cls.directory / 'daemon.log', 'w+')
        cls.process = subprocess.Popen([sys.executable, str(ROOT / 'typebridge-ubuntu.py')],
            env=dict(os.environ, TYPEBRIDGE_CONFIG_DIR=str(cls.directory)), stdout=cls.log, stderr=cls.log)
        for _ in range(100):
            if cls.process.poll() is not None:
                cls.log.seek(0)
                raise RuntimeError(cls.log.read())
            try:
                peer = Peer(cls.port)
                peer.close()
                break
            except OSError:
                time.sleep(.05)

    @classmethod
    def tearDownClass(cls):
        cls.process.terminate()
        cls.process.wait(timeout=5)
        cls.log.seek(0)
        log = cls.log.read()
        (ROOT / 'test-linux-daemon.log').write_text(log)
        cls.log.close()
        cls.temp.cleanup()
        if 'Traceback' in log or 'CRITICAL' in log or 'Fatal Python' in log:
            raise AssertionError(log)

    def setUp(self):
        self.peer = Peer(self.port)
        self.peer.until('S ')

    def tearDown(self):
        self.peer.close()
        time.sleep(.1)

    def start_reverse(self):
        subprocess.run(['xdotool', 'key', 'ctrl+alt+u'], check=True)
        self.peer.until('RGW 1 ')
        self.peer.send('RGW ok')
        time.sleep(.15)

    def test_fragmented_heartbeat_and_directory(self):
        for byte in b'PING\n':
            self.peer.sock.sendall(bytes([byte]))
        self.assertEqual(self.peer.until('PONG'), 'PONG')
        self.peer.send('WHERE')
        self.assertEqual(self.peer.until('DIR '), 'DIR ' + str(self.directory / 'received'))
        self.peer.send('DIR /etc')
        self.peer.send('WHERE')
        self.assertEqual(self.peer.until('DIR '), 'DIR ' + str(self.directory / 'received'))

    def test_reverse_input_and_return(self):
        self.start_reverse()
        subprocess.run(['xdotool', 'key', 'a', 'click', '8', 'click', '4'], check=True)
        self.peer.until('RK a down')
        self.peer.until('RK a up')
        self.peer.until('RB 8 down')
        self.peer.until('RW up')
        subprocess.run(['xdotool', 'key', 'ctrl+alt+u'], check=True)
        self.assertEqual(self.peer.until('RGW 0'), 'RGW 0')
        time.sleep(.6)

    def test_reverse_timeout_releases_grab(self):
        subprocess.run(['xdotool', 'key', 'ctrl+alt+u'], check=True)
        self.peer.until('RGW 1 ')
        time.sleep(2.2)
        self.start_reverse()
        self.peer.send('RGW 0')
        time.sleep(.6)

    def test_reverse_fast_motion_and_recenter(self):
        self.start_reverse()
        # A genuine fast stroke may exceed both old 80px/60px heuristics.
        # Drive the real X server, event loop and control socket together.
        subprocess.run(['xdotool', 'mousemove', '--sync', '0', '400'], check=True)
        self.assertEqual(self.peer.until('RM '), 'RM -640 0')
        subprocess.run(['xdotool', 'mousemove_relative', '--sync', '--', '-10', '0'], check=True)
        self.assertEqual(self.peer.until('RM '), 'RM -10 0')
        self.peer.send('RGW 0')
        time.sleep(.6)

    def test_reverse_disconnect_releases_grab(self):
        self.start_reverse()
        self.peer.close()
        time.sleep(.6)
        self.peer = Peer(self.port)
        self.peer.until('S ')
        self.start_reverse()
        self.peer.send('RGW 0')
        time.sleep(.6)

    def test_forward_keyboard_and_mouse(self):
        import gi
        gi.require_version('Gtk', '3.0')
        gi.require_version('GdkX11', '3.0')
        from gi.repository import Gtk, GdkX11, GLib
        window = Gtk.Window()
        entry = Gtk.Entry()
        window.add(entry)
        window.show_all()
        def pump():
            until = time.monotonic() + .3
            while time.monotonic() < until:
                while GLib.MainContext.default().pending():
                    GLib.MainContext.default().iteration(False)
                time.sleep(.002)
        try:
            pump()
            subprocess.run(['xdotool', 'windowfocus', str(window.get_window().get_xid())], check=True)
            entry.grab_focus()
            self.peer.send('T "abc"')
            self.peer.send('PING')
            self.peer.until('PONG')
            pump()
            self.assertEqual(entry.get_text(), 'abc')
            self.peer.send('P 500 400')
            self.peer.send('M 17 -9')
            self.peer.send('PING')
            self.peer.until('PONG')
            position = subprocess.check_output(['xdotool', 'getmouselocation', '--shell']).decode()
            self.assertIn('X=517', position)
            self.assertIn('Y=391', position)
        finally:
            window.destroy()
            pump()

    def transfer(self, header, data=b''):
        with socket.create_connection(('127.0.0.1', self.file_port), 3) as sock:
            sock.sendall((header + '\n').encode() + data)
            sock.shutdown(socket.SHUT_WR)
            return sock.makefile('rb').readline().decode().strip()

    def test_file_resume_unicode_and_numeric_suffix(self):
        data = bytes(range(256)) * 512
        name = '测试 文件 2'
        self.assertEqual(self.transfer('RESUME %d %s' % (len(data), name)), 'HAVE 0')
        self.assertEqual(self.transfer('UPLOAD %d %s' % (len(data), name), data[:9000]), 'ERR incomplete')
        self.assertEqual(self.transfer('RESUME %d %s' % (len(data), name)), 'HAVE 9000')
        self.assertTrue(self.transfer('UPLOAD %d %s 9000' % (len(data), name), data[9000:]).startswith('OK '))
        self.assertEqual((self.directory / 'received' / name).read_bytes(), data)
        self.assertFalse((self.directory / 'received' / (name + '.part')).exists())

    def test_file_name_traversal_sanitized(self):
        self.assertTrue(self.transfer('UPLOAD 3 ../escape.txt', b'abc').startswith('OK '))
        self.assertEqual((self.directory / 'received' / 'escape.txt').read_bytes(), b'abc')
        self.assertFalse((self.directory / 'escape.txt').exists())

    def test_clipboard_text_and_image(self):
        import gi
        gi.require_version('Gtk', '3.0')
        from gi.repository import Gtk, Gdk, GdkPixbuf
        self.peer.send('C ' + json.dumps('跨机 clipboard 🙂'))
        self.peer.send('PING')
        self.peer.until('PONG')
        clipboard = Gtk.Clipboard.get(Gdk.SELECTION_CLIPBOARD)
        self.assertEqual(clipboard.wait_for_text(), '跨机 clipboard 🙂')
        pixbuf = GdkPixbuf.Pixbuf.new(GdkPixbuf.Colorspace.RGB, True, 8, 3, 2)
        pixbuf.fill(0x123456ff)
        _, png = pixbuf.save_to_bufferv('png', [], [])
        self.assertEqual(self.transfer('CLIPIMG %d 0' % len(png), png), 'OK clipboard')
        time.sleep(.15)
        actual = clipboard.wait_for_image()
        self.assertIsNotNone(actual)
        self.assertEqual((actual.get_width(), actual.get_height()), (3, 2))

    def test_ubuntu_recopy_after_remote_change_reaches_control_socket(self):
        import gi
        gi.require_version('Gtk', '3.0')
        from gi.repository import Gtk, Gdk, GLib
        clipboard = Gtk.Clipboard.get(Gdk.SELECTION_CLIPBOARD)
        self.peer.send('C ' + json.dumps('remote original'))
        self.peer.send('PING')
        self.peer.until('PONG')
        self.assertEqual(clipboard.wait_for_text(), 'remote original')

        def expect_copy(value):
            clipboard.set_text(value, -1)
            deadline = time.monotonic() + 2
            while time.monotonic() < deadline:
                while GLib.MainContext.default().pending():
                    GLib.MainContext.default().iteration(False)
                if select.select([self.peer.sock], [], [], .01)[0]:
                    self.peer.buf += self.peer.sock.recv(65536)
                while b'\n' in self.peer.buf:
                    line, self.peer.buf = self.peer.buf.split(b'\n', 1)
                    if line.startswith(b'C ') and json.loads(line[2:]) == value:
                        return
            self.fail('Ubuntu clipboard did not reach Windows control connection: ' + value)

        expect_copy('local replacement')
        expect_copy('remote original')

    def test_reverse_grab_blocks_local_clicks(self):
        import gi
        gi.require_version('Gtk', '3.0')
        from gi.repository import Gtk, GLib
        window = Gtk.Window()
        button = Gtk.Button(label='local click target')
        clicks = []
        button.connect('clicked', lambda *_: clicks.append(True))
        window.add(button)
        window.set_default_size(1280, 800)
        window.show_all()
        def pump():
            deadline = time.monotonic() + .2
            while time.monotonic() < deadline:
                while GLib.MainContext.default().pending():
                    GLib.MainContext.default().iteration(False)
                time.sleep(.002)
        try:
            pump()
            self.start_reverse()
            subprocess.run(['xdotool', 'click', '1'], check=True)
            self.peer.until('RB 1 down')
            self.peer.until('RB 1 up')
            pump()
            self.assertEqual(clicks, [], 'a forwarded click also activated the Ubuntu button')
            self.peer.send('RGW 0')
            self.peer.send('PING')
            self.peer.until('PONG')
            pump()
        finally:
            window.destroy()
            pump()

    def test_invalid_frame_disconnects_only_its_client(self):
        other = Peer(self.port)
        try:
            other.until('S ')
            other.sock.sendall(b'\xff\n')
            with self.assertRaises((EOFError, ConnectionResetError)):
                other.until('PONG')
            self.peer.send('PING')
            self.assertEqual(self.peer.until('PONG'), 'PONG')
        finally:
            other.close()


if __name__ == '__main__':
    unittest.main()
