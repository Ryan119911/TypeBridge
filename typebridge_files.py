"""Streaming file and image transfers using the existing wire protocol."""
import json
import os
import re
import socket
import threading
import time
import weakref

FILE_MAX_NAME = 180
CLIPIMG_MAX_BYTES = 64 << 20      # 剪贴板图片只进内存，必须有上限
CLIPIMG_MAX_TEXT = 1 << 20


def safe_name(name):
    """只保留文件名本体，去掉路径和危险字符，避免写到别处。"""
    name = os.path.basename((name or "").replace("\\", "/")).strip()
    name = re.sub(r'[\x00-\x1f<>:"/\\|?*]', "_", name)
    if name in ("", ".", ".."):
        name = "file"
    if len(name) > FILE_MAX_NAME:
        root, ext = os.path.splitext(name)
        name = root[:FILE_MAX_NAME - len(ext)] + ext
    return name


def unique_path(directory, name):
    """同名只保留一份：直接覆盖，不再生成 (1)、(2) 副本。"""
    return os.path.join(directory, name)


def valid_file_dir(target):
    """拒绝路径穿越和系统目录：控制端口没有认证，DIR 是可被任意对端改写的持久化配置。"""
    if ".." in target.replace("\\", "/").split("/"):
        return False
    path = os.path.abspath(os.path.expanduser(target))
    blocked = ("/etc", "/usr", "/bin", "/sbin", "/lib", "/proc", "/sys",
               "/dev", "/boot", "/root", "/var", "/opt")
    for prefix in blocked:
        if path == prefix or path.startswith(prefix + os.sep):
            return False
    return True


def part_meta_path(dest):
    return dest + ".part.info"


_PART_LOCKS = weakref.WeakValueDictionary()
_PART_LOCKS_GUARD = threading.Lock()


def part_lock_for(dest):
    with _PART_LOCKS_GUARD:
        lock = _PART_LOCKS.get(dest)
        if lock is None:
            lock = threading.Lock()
            _PART_LOCKS[dest] = lock
        return lock


def resume_offset(dest, name, size):
    """已经收到多少字节可以接着传。文件名或总大小对不上就返回 0，绝不能拼错文件。"""
    part = dest + ".part"
    try:
        with open(part_meta_path(dest), "r", encoding="utf-8") as fh:
            meta = json.load(fh)
    except Exception:
        return 0
    if meta.get("name") != name or meta.get("size") != size:
        return 0
    try:
        have = os.path.getsize(part)
    except OSError:
        return 0
    if have <= 0 or have >= size:
        return 0
    return have


class FileReceiver:
    def __init__(self, directory, clipboard, notify, log):
        self.directory = directory
        self.clipboard = clipboard
        self.notify = notify
        self.log = log

    def receive(self, conn, addr):
        stream = None
        try:
            conn.settimeout(120)  # 空闲 120s 才判死：大文件+慢链路别动不动断
            stream = conn.makefile("rb")
            header = stream.readline(4096).decode("utf-8", "replace").strip()
            parts = header.split(" ", 2)
            if len(parts) != 3 or parts[0] not in ("UPLOAD", "CLIPIMG", "RESUME"):
                self.log("file: bad header %r from %s" % (header[:60], addr[0]))
                conn.sendall(b"ERR bad header\n")
                return
            try:
                size = int(parts[1])
            except ValueError:
                conn.sendall(b"ERR bad size\n")
                return
            if size < 0:
                conn.sendall(b"ERR bad size\n")
                return

            if parts[0] == "RESUME":
                # 断点续传握手：告诉发送方 .part 里已经有几个字节。
                # 头是 "RESUME <总大小> <文件名>"，第三段整体就是文件名、没有偏移字段；
                # 原来按空格 rpartition 会把 "IMG 2" 截成 "IMG"，与 UPLOAD 分支不一致。
                name = safe_name(parts[2])
                dest = unique_path(self.directory(name), name)
                with part_lock_for(dest):
                    have = resume_offset(dest, name, size)
                self.log("resume query: %s have %d/%d" % (name, have, size))
                conn.sendall(("HAVE %d\n" % have).encode("utf-8"))
                return

            if parts[0] == "CLIPIMG":
                if size > CLIPIMG_MAX_BYTES:
                    conn.sendall(b"ERR clipboard too large\n")
                    return
                # 剪贴板图片：只进内存，不落盘。
                # 第三段是可选文本长度；旧版客户端这里放的是文件名，解析失败按 0 处理。
                try:
                    caption_len = int(parts[2])
                except ValueError:
                    caption_len = 0
                if caption_len < 0:
                    caption_len = 0
                if caption_len > CLIPIMG_MAX_TEXT:
                    conn.sendall(b"ERR clipboard too large\n")
                    return

                data = bytearray()
                while len(data) < size:
                    chunk = stream.read(min(1 << 16, size - len(data)))
                    if not chunk:
                        break
                    data += chunk
                if len(data) != size:
                    self.log("clipboard image: incomplete (%d/%d) from %s" % (len(data), size, addr[0]))
                    conn.sendall(b"ERR incomplete\n")
                    return

                caption = bytearray()
                while len(caption) < caption_len:
                    chunk = stream.read(min(1 << 16, caption_len - len(caption)))
                    if not chunk:
                        break
                    caption += chunk
                text = caption.decode("utf-8", "replace")

                # GTK 剪贴板只能在主循环线程里操作，这里排队过去
                if len(caption) != caption_len:
                    conn.sendall(b"ERR incomplete\n")
                    return
                self.clipboard(bytes(data), text)
                conn.sendall(b"OK clipboard\n")
                return

            # 第四段是可选的续传偏移。文件名本身可能以「空格 + 数字」结尾（如 IMG 2），
            # 只有确实存在匹配的断点记录时才把尾段当偏移，否则整段都当文件名。
            raw = parts[2]
            name, _, tail = raw.rpartition(" ")
            offset = 0
            if tail.isdigit() and int(tail) > 0:
                candidate = safe_name(name)
                candidate_dest = unique_path(self.directory(candidate), candidate)
                if resume_offset(candidate_dest, candidate, size) == int(tail):
                    offset = int(tail)
                    name = candidate
                else:
                    name = raw
            else:
                name = raw
            name = safe_name(name)
            directory = self.directory(name)
            os.makedirs(directory, exist_ok=True)
            dest = unique_path(directory, name)
            temp_dest = dest + ".part"

            with part_lock_for(dest):
                if offset > 0:
                    try:
                        have = os.path.getsize(temp_dest)
                    except OSError:
                        have = 0
                    if have < offset or offset > size:
                        self.log("file: %s 断点 %d 与本地 %d 不符，改为从头传" % (name, offset, have))
                        offset = 0

                if offset > 0:
                    self.log("file: %s 从 %d/%d (%d%%) 续传"
                        % (name, offset, size, offset * 100 // max(1, size)))

                started = time.time()
                received = offset
                last_mark = offset
                mode = "r+b" if offset > 0 else "wb"
                with open(temp_dest, mode) as out:
                    if offset > 0:
                        out.seek(offset)
                        out.truncate(offset)
                    else:
                        try:
                            with open(part_meta_path(dest), "w", encoding="utf-8") as meta:
                                json.dump({"name": name, "size": size}, meta)
                        except OSError:
                            pass
                    while received < size:
                        chunk = stream.read(min(1 << 18, size - received))
                        if not chunk:
                            break
                        out.write(chunk)
                        received += len(chunk)
                        # 大文件按 25% 打点，出问题能看出断在第几段
                        if size >= (64 << 20) and received - last_mark >= size // 4:
                            last_mark = received
                            self.log("file: %s %d%% (%d/%d MB)"
                                % (name, received * 100 // size, received >> 20, size >> 20))

                if received != size:
                    # 保留 .part 和 .part.info：下次同一个文件重发就能接着传
                    self.log("file: incomplete %s (%d/%d) from %s，断点已保留"
                        % (name, received, size, addr[0]))
                    conn.sendall(b"ERR incomplete\n")
                    return

                # 原子替换：要么是完整的旧文件，要么是完整的新文件，不会留下半截或副本
                os.replace(temp_dest, dest)
                try:
                    os.remove(part_meta_path(dest))
                except OSError:
                    pass

                elapsed = max(0.001, time.time() - started)
                self.log("file <- %s (%d bytes, 本次 %d bytes, %.1f MB/s%s) -> %s"
                    % (name, size, size - offset, (size - offset) / elapsed / 1048576.0,
                       "，续传" if offset else "", dest))
                # 桌面提醒：接收方也要知道文件落在哪个目录，不然只能自己翻
                self.notify("收到文件", "%s\n→ %s" % (name, os.path.dirname(dest)))
                conn.sendall(("OK " + dest + "\n").encode("utf-8"))
        except Exception as e:
            self.log("file upload failed: %r" % (e,))
            try:
                conn.sendall(b"ERR exception\n")
            except OSError:
                pass
        finally:
            if stream is not None:
                stream.close()
            try:
                conn.close()
            except OSError:
                pass


    def accept_loop(self, sock):
        while True:
            try:
                conn, addr = sock.accept()
            except OSError:
                return
            threading.Thread(target=self.receive, args=(conn, addr), daemon=True).start()



def push_file(path, host, port, log):
    size = os.path.getsize(path)
    name = os.path.basename(path)
    offset = 0

    # 先问对端已有多少字节；旧版接收端回 ERR bad header 时按从头传走老协议。
    # 与 Windows -> Ubuntu 方向用同一套 HAVE / UPLOAD ... <offset> 约定。
    try:
        probe = socket.create_connection((host, port), 5)
        try:
            probe.settimeout(5)
            probe.sendall(("RESUME %d %s\n" % (size, name)).encode("utf-8"))
            answer = probe.makefile("rb").readline(4096).decode("utf-8", "replace").strip()
            if answer.startswith("HAVE "):
                have = int(answer[5:].strip())
                if 0 < have < size:
                    offset = have
        finally:
            probe.close()
    except Exception as e:
        log("outbox resume probe failed: %r" % (e,))

    sock = socket.create_connection((host, port), 5)
    try:
        # 只有 connect 超时不够：对端僵住时 sendall / readline 会永久占住 in-flight
        sock.settimeout(60)
        header = "UPLOAD %d %s" % (size, name)
        if offset:
            header += " %d" % offset
        sock.sendall((header + "\n").encode("utf-8"))
        with open(path, "rb") as source:
            if offset:
                source.seek(offset)
            while True:
                chunk = source.read(1 << 16)
                if not chunk:
                    break
                sock.sendall(chunk)
        return sock.makefile("rb").readline(4096).decode("utf-8", "replace").strip()
    finally:
        try:
            sock.close()
        except OSError:
            pass
