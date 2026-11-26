using System;
using System.Runtime.InteropServices;

namespace TypeBridgeApp
{
    // Runs on the UI thread. Retain only the newest incoming clipboard value.
    internal sealed class ClipboardWriteQueue
    {
        private Action pending;
        private uint sourceSequence;
        private int attempts;
        private readonly Action<string> log;
        internal bool Pending { get { return pending != null; } }

        internal ClipboardWriteQueue(Action<string> logger) { log = logger; }

        internal void Enqueue(Action write, uint sequence)
        {
            pending = write;
            sourceSequence = sequence;
            attempts = 0;
            Retry(sequence);
        }

        internal void Retry(uint sequence)
        {
            if (pending == null) return;
            if (sequence != sourceSequence)
            {
                pending = null;
                log("clipboard retry cancelled: newer local copy");
                return;
            }
            try
            {
                pending();
                pending = null;
            }
            catch (ExternalException)
            {
                attempts++;
                if (attempts >= 10)
                {
                    pending = null;
                    log("clipboard write failed after 10 attempts");
                }
                else if (attempts == 1)
                    log("clipboard busy: queued for retry");
            }
            catch (Exception error)
            {
                pending = null;
                log("clipboard write failed: " + error.Message);
            }
        }
    }
}
