using System;
using System.Diagnostics;
using System.Threading;
using MelonLoader;

namespace WithUMM.Diagnostics
{
    internal sealed class BootTrace
    {
        private readonly Stopwatch watch = Stopwatch.StartNew();
        private readonly MelonLogger.Instance log;
        internal BootTrace(MelonLogger.Instance log) { this.log = log; }
        internal void Event(string text) => log.Msg("+" + watch.ElapsedMilliseconds + "ms [thread " + Thread.CurrentThread.ManagedThreadId + "] " + text);
        internal void Warning(string text) => log.Warning(text);
        internal void Error(string text) => log.Error(text);
        internal void Failure(string phase, Exception e) => log.Error(phase + ": " + ReflectionAccess.Unwrap(e));
    }
}
