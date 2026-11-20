import pathlib
import socket
import sys
import tempfile
import threading
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))
from typebridge_files import FileReceiver, push_file


class FileTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = pathlib.Path(self.temp.name)
        self.images = []
        self.receiver = FileReceiver(lambda name='': str(self.root),
            lambda data, text: self.images.append((data, text)), lambda *args: None, lambda text: None)

    def tearDown(self):
        self.temp.cleanup()

    def transfer(self, header, data=b''):
        client, server = socket.socketpair()
        worker = threading.Thread(target=self.receiver.receive, args=(server, ('test', 0)))
        worker.start()
        with client:
            client.settimeout(5)
            client.sendall(header.encode() + b'\n' + data)
            client.shutdown(socket.SHUT_WR)
            reply = client.recv(4096).decode().strip()
        worker.join(5)
        self.assertFalse(worker.is_alive())
        return reply

    def test_resume_unicode_and_numeric_names(self):
        data = bytes(range(256)) * 100
        for name in ('测试 文件 2', 'file 0'):
            self.assertEqual(self.transfer('UPLOAD %d %s' % (len(data), name), data[:999]), 'ERR incomplete')
            self.assertEqual(self.transfer('RESUME %d %s' % (len(data), name)), 'HAVE 999')
            self.assertTrue(self.transfer('UPLOAD %d %s 999' % (len(data), name), data[999:]).startswith('OK '))
            self.assertEqual((self.root / name).read_bytes(), data)

    def test_image_caption_must_be_complete(self):
        self.assertEqual(self.transfer('CLIPIMG 3 5', b'pngab'), 'ERR incomplete')
        self.assertEqual(self.images, [])
        self.assertEqual(self.transfer('CLIPIMG 3 2', b'pngab'), 'OK clipboard')
        self.assertEqual(self.images, [(b'png', 'ab')])

    def test_empty_file_and_traversal(self):
        self.assertTrue(self.transfer('UPLOAD 0 ../empty').startswith('OK '))
        self.assertEqual((self.root / 'empty').read_bytes(), b'')

    def test_sender_and_receiver(self):
        source = self.root / 'source'
        source.write_bytes(b'actual TCP file transfer' * 1000)
        destination = self.root / 'received'
        destination.mkdir()
        receiver = FileReceiver(lambda name='': str(destination), lambda *args: None, lambda *args: None, lambda text: None)
        listener = socket.socket()
        with listener:
            listener.bind(('127.0.0.1', 0))
            listener.listen(2)
            def serve():
                for _ in range(2):
                    conn, addr = listener.accept()
                    receiver.receive(conn, addr)
            worker = threading.Thread(target=serve)
            worker.start()
            result = push_file(str(source), '127.0.0.1', listener.getsockname()[1], lambda text: None)
            worker.join(5)
        self.assertTrue(result.startswith('OK '))
        self.assertEqual((destination / 'source').read_bytes(), source.read_bytes())


if __name__ == '__main__':
    unittest.main()
