using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace TypeBridgeApp
{
    internal sealed class LineFramer
    {
        internal const int MaxBytes = 1 << 20;
        private byte[] pending = new byte[8192];
        private int length;
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

        internal void Feed(byte[] bytes, int count, Action<string> receive)
        {
            for (int i = 0; i < count; i++)
            {
                byte value = bytes[i];
                if (value == 10)
                {
                    int size = length > 0 && pending[length - 1] == 13 ? length - 1 : length;
                    string line = Utf8.GetString(pending, 0, size);
                    length = 0;
                    if (line.Length != 0) receive(line);
                    continue;
                }
                if (length == MaxBytes) throw new InvalidDataException("Control frame exceeds 1 MiB");
                if (length == pending.Length) Array.Resize(ref pending, Math.Min(MaxBytes, length * 2));
                pending[length++] = value;
            }
        }
    }

    // One queue belongs to one connection. Closing wakes a blocked sender and
    // discards input that must never be replayed into a later connection.
    internal sealed class ControlQueue : IDisposable
    {
        internal const int MaxLines = 4096;
        internal const int MaxCharacters = 2 << 20;
        private readonly Queue<string> lines = new Queue<string>();
        private int characters;
        private bool closed;

        internal bool TryAdd(string line)
        {
            lock (lines)
            {
                if (closed || lines.Count >= MaxLines || line.Length > MaxCharacters - characters)
                    return false;
                lines.Enqueue(line);
                characters += line.Length;
                Monitor.Pulse(lines);
                return true;
            }
        }

        internal string Take()
        {
            lock (lines)
            {
                while (!closed && lines.Count == 0) Monitor.Wait(lines);
                if (closed) return null;
                string line = lines.Dequeue();
                characters -= line.Length;
                return line;
            }
        }

        public void Dispose()
        {
            lock (lines)
            {
                closed = true;
                lines.Clear();
                characters = 0;
                Monitor.PulseAll(lines);
            }
        }
    }
}
