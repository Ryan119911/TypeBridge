import pathlib
import socket
import sys
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))
from typebridge_protocol import LineBuffer, OutputBuffer, MAX_FRAME, MAX_OUTPUT


class ProtocolTests(unittest.TestCase):
    def test_partial_write_then_would_block(self):
        class SlowSocket:
            def __init__(self):
                self.calls = 0
                self.received = bytearray()

            def send(self, data):
                self.calls += 1
                if self.calls == 2:
                    raise BlockingIOError()
                sent = min(3, len(data))
                self.received.extend(data[:sent])
                return sent

        sock = SlowSocket()
        out = OutputBuffer()
        out.append('中文\n'.encode())
        out.append(b'PING\n')
        self.assertFalse(out.flush(sock))
        self.assertEqual(out.size, len('中文\n'.encode() + b'PING\n') - 3)
        self.assertTrue(out.flush(sock))
        self.assertEqual(sock.received, '中文\n'.encode() + b'PING\n')

    def test_fragmented_unicode_and_batched_frames(self):
        buffer = LineBuffer()
        lines = []
        for value in 'PING\nC "中文🙂"\r\n'.encode():
            lines.extend(buffer.feed(bytes([value])))
        self.assertEqual(lines, ['PING', 'C "中文🙂"'])
        self.assertEqual(list(buffer.feed(b'x' * MAX_FRAME + b'\nPING\n')), ['x' * MAX_FRAME, 'PING'])

    def test_limits_and_invalid_utf8(self):
        with self.assertRaises(ValueError):
            list(LineBuffer().feed(b'x' * (MAX_FRAME + 1)))
        with self.assertRaises(UnicodeError):
            list(LineBuffer().feed(b'\xff\n'))
        out = OutputBuffer()
        out.append(b'x' * MAX_OUTPUT)
        with self.assertRaises(ValueError):
            out.append(b'x')
        self.assertEqual(out.size, MAX_OUTPUT)

    def test_backpressure_preserves_order(self):
        writer, reader = socket.socketpair()
        with writer, reader:
            writer.setblocking(False)
            reader.setblocking(False)
            writer.setsockopt(socket.SOL_SOCKET, socket.SO_SNDBUF, 4096)
            out = OutputBuffer()
            data = bytes(range(256)) * 4096
            out.append(data)
            out.append(b'PING\n')
            out.flush(writer)
            received = bytearray()
            while out.size:
                try:
                    received.extend(reader.recv(65536))
                except BlockingIOError:
                    pass
                out.flush(writer)
            while True:
                try:
                    received.extend(reader.recv(65536))
                except BlockingIOError:
                    break
            self.assertEqual(received, data + b'PING\n')
            self.assertFalse(out.chunks)


if __name__ == '__main__':
    unittest.main()
