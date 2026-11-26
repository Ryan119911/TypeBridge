"""Replay real daemon callbacks when a losing connection probe closes."""
import importlib.util
import os
import pathlib
import socket
import sys
import tempfile
import threading
import types
import unittest
from unittest.mock import patch

ROOT = pathlib.Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
from typebridge_files import FileReceiver, push_file


@unittest.skipUnless(sys.platform == 'linux' and os.environ.get('DISPLAY') == ':97',
                     'requires private Xvfb :97')
class PeerRoutingTests(unittest.TestCase):
    ACTIVE = '192.0.2.10'
    PROBE = '192.0.2.11'

    @classmethod
    def setUpClass(cls):
        spec = importlib.util.spec_from_file_location('routing_daemon', ROOT / 'typebridge-ubuntu.py')
        cls.daemon = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(cls.daemon)

    def setUp(self):
        self.d = self.daemon
        self.d.CONNS.clear()
        self.d.LAST_PEER.clear()
        self.d.LAST_PEER['addr'] = None
        self.d.OUTBOX_NEXT_TRY.clear()
        self.d.OUTBOX_INFLIGHT.clear()
        config = patch.dict(self.d.CONF, file_send_host='')
        config.start()
        self.addCleanup(config.stop)
        self.connections = []
        for name in ('refresh_tray', 'set_pointer_visible', 'release_mouse_buttons', 'flush_move'):
            stub = patch.object(self.d, name)
            stub.start()
            self.addCleanup(stub.stop)
        self.temp = tempfile.TemporaryDirectory(prefix='typebridge-routing-')
        self.addCleanup(self.temp.cleanup)
        self.root = pathlib.Path(self.temp.name)

    def tearDown(self):
        for client, server, addr in reversed(self.connections):
            if server.fileno() >= 0:
                self.d.drop(server.fileno(), 'test disconnect')
            client.close()

    def connect(self, address, confirmed=False):
        client, server = socket.socketpair()
        client.settimeout(2)
        addr = (address, 12345)
        connection = (client, server, addr)
        self.connections.append(connection)
        # The accepted peer address is the only network fixture. Reads, framing,
        # dispatch, disconnect, and the upload worker use the real daemon code.
        with patch.object(self.d, 'srv', types.SimpleNamespace(accept=lambda: (server, addr))):
            self.d.on_accept(None, None)
        self.assertTrue(client.recv(4096).startswith(b'S '))
        if confirmed:
            self.send(connection, 'SW 2560 1440 left')
        return connection

    def send(self, connection, line):
        client, server, addr = connection
        client.sendall((line + '\n').encode())
        self.d.on_data(server.fileno(), self.d.GLib.IO_IN, server, addr)

    def disconnect(self, connection):
        client, server, addr = connection
        client.close()
        self.assertFalse(self.d.on_data(server.fileno(), self.d.GLib.IO_IN, server, addr))

    def peer(self):
        connection = self.connect('127.0.0.1')
        self.send(connection, 'PEER')
        answer = connection[0].recv(4096).decode().strip()
        self.disconnect(connection)
        return answer

    def test_probe_does_not_replace_selected_peer_while_open(self):
        self.connect(self.ACTIVE, confirmed=True)
        probe = self.connect(self.PROBE)
        self.send(probe, 'PING')
        self.assertEqual(probe[0].recv(4096), b'PONG\n')
        self.assertEqual(self.peer(), 'PEER ' + self.ACTIVE)

    def test_installer_reaches_receiver_after_losing_probe_closes(self):
        self.connect(self.ACTIVE, confirmed=True)
        probe = self.connect(self.PROBE)
        self.send(probe, 'PING')
        probe[0].recv(4096)
        self.disconnect(probe)
        self.assert_installer_transfer()

    def assert_installer_transfer(self):
        source = self.root / '安装包 setup.exe'
        data = b'MZ' + bytes(range(256)) * 4096
        source.write_bytes(data)
        received = self.root / 'received'
        received.mkdir()
        receiver = FileReceiver(lambda name='': str(received), lambda *args: None,
                                lambda *args: None, lambda *args: None)
        with socket.socket() as listener:
            listener.bind(('127.0.0.1', 0))
            listener.listen(2)
            listener.settimeout(1)
            port = listener.getsockname()[1]
            failures = []

            def serve():
                try:
                    for _ in range(2):
                        conn, addr = listener.accept()
                        receiver.receive(conn, addr)
                except OSError as error:
                    failures.append(error)

            def route(path, host, ignored_port, log):
                if host != self.ACTIVE:
                    raise ConnectionError('file sent to disconnected probe address: ' + host)
                return push_file(path, '127.0.0.1', port, log)

            worker = threading.Thread(target=serve, daemon=True)
            worker.start()
            with patch.object(self.d, 'push_file', side_effect=route):
                self.d.outbox_worker(str(source))
            worker.join(3)
            self.assertFalse(worker.is_alive(), 'file receiver did not finish')
        target = received / source.name
        self.assertTrue(target.exists(), 'installer never reached the selected Windows receiver')
        self.assertEqual(target.read_bytes(), data)
        self.assertEqual((self.root / '已发送' / source.name).read_bytes(), data)
        self.assertFalse(source.exists())
        self.assertEqual(failures, [])

    def test_installer_uses_configured_route_when_control_return_path_is_unreachable(self):
        self.connect(self.PROBE, confirmed=True)
        self.d.CONF['file_send_host'] = self.ACTIVE
        self.assert_installer_transfer()

    def test_peer_query_uses_configured_file_route(self):
        self.connect(self.PROBE, confirmed=True)
        self.d.CONF['file_send_host'] = self.ACTIVE
        self.assertEqual(self.peer(), 'PEER ' + self.ACTIVE)

    def test_configured_route_waits_for_control_connection(self):
        self.d.CONF['file_send_host'] = self.ACTIVE
        self.assertEqual(self.peer(), 'PEER 0.0.0.0')

    def test_disconnected_control_is_not_reused_for_outbox(self):
        active = self.connect(self.ACTIVE, confirmed=True)
        self.disconnect(active)
        source = self.root / 'setup.msi'
        source.write_bytes(b'installer test data')
        with patch.object(self.d, 'push_file') as send:
            self.d.outbox_worker(str(source))
        send.assert_not_called()
        self.assertTrue(source.exists())
        self.assertEqual(self.peer(), 'PEER 0.0.0.0')

    def test_reconnect_changes_peer_and_old_disconnect_keeps_it(self):
        old = self.connect(self.ACTIVE, confirmed=True)
        self.connect(self.PROBE, confirmed=True)
        self.disconnect(old)
        self.assertEqual(self.peer(), 'PEER ' + self.PROBE)

    def test_disconnected_new_control_falls_back_to_live_confirmed_peer(self):
        self.connect(self.ACTIVE, confirmed=True)
        new = self.connect(self.PROBE, confirmed=True)
        self.disconnect(new)
        self.assertEqual(self.peer(), 'PEER ' + self.ACTIVE)

    def test_probe_without_confirmed_control_is_not_file_target(self):
        self.connect(self.PROBE)
        self.assertEqual(self.peer(), 'PEER 0.0.0.0')

    def test_malformed_screen_announcement_does_not_select_peer(self):
        self.connect(self.ACTIVE, confirmed=True)
        probe = self.connect(self.PROBE)
        for line in ('SW bad 1440 left', 'SW 0 1440 left', 'SW 2560 1440 invalid'):
            self.send(probe, line)
            self.assertEqual(self.peer(), 'PEER ' + self.ACTIVE)

    def test_clipboard_image_uses_selected_peer_after_probe(self):
        self.connect(self.ACTIVE, confirmed=True)
        probe = self.connect(self.PROBE)
        self.disconnect(probe)
        with patch.object(self.d.socket, 'create_connection') as create:
            create.return_value.makefile.return_value.readline.return_value = b'OK clipboard\n'
            self.d.push_clipboard_image(b'png fixture')
        self.assertEqual(create.call_args.args[0][0], self.ACTIVE)

    def test_clipboard_image_uses_configured_route(self):
        self.connect(self.PROBE, confirmed=True)
        self.d.CONF['file_send_host'] = self.ACTIVE
        with patch.object(self.d.socket, 'create_connection') as create:
            create.return_value.makefile.return_value.readline.return_value = b'OK clipboard\n'
            self.d.push_clipboard_image(b'png fixture')
        self.assertEqual(create.call_args.args[0][0], self.ACTIVE)


if __name__ == '__main__':
    unittest.main()
