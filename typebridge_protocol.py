"""Bounded control framing and nonblocking output without polling."""
from collections import deque

MAX_FRAME = 1 << 20
MAX_OUTPUT = 4 << 20


class LineBuffer:
    def __init__(self):
        self.pending = bytearray()

    def feed(self, data):
        fragments = data.split(b"\n")
        for fragment in fragments[:-1]:
            if len(self.pending) + len(fragment) > MAX_FRAME:
                raise ValueError("control frame too large")
            self.pending.extend(fragment)
            line = self.pending.decode("utf-8").rstrip("\r")
            self.pending.clear()
            yield line
        tail = fragments[-1]
        if len(self.pending) + len(tail) > MAX_FRAME:
            raise ValueError("control frame too large")
        self.pending.extend(tail)


class OutputBuffer:
    def __init__(self):
        self.chunks = deque()
        self.size = 0

    def append(self, data):
        if len(data) > MAX_OUTPUT - self.size:
            raise ValueError("control output overflow")
        if data:
            self.chunks.append(memoryview(data))
            self.size += len(data)

    def flush(self, sock):
        while self.chunks:
            chunk = self.chunks[0]
            try:
                sent = sock.send(chunk)
            except (BlockingIOError, InterruptedError):
                return False
            if sent <= 0:
                raise OSError("control connection closed")
            self.size -= sent
            if sent < len(chunk):
                self.chunks[0] = chunk[sent:]
            else:
                self.chunks.popleft()
        return True
