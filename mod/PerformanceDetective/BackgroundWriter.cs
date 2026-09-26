using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;

namespace PerformanceDetective
{
    /// <summary>
    /// All disk writes happen on this low-priority thread, so the game's main thread never waits for the disk.
    /// </summary>
    internal sealed class BackgroundWriter : IDisposable
    {
        private readonly BlockingCollection<Action> _work = new BlockingCollection<Action>();
        private readonly Thread _thread;

        public BackgroundWriter()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "PerformanceDetective writer", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }

        public void Append(string path, string text) =>
            _work.Add(() => File.AppendAllText(path, text, new UTF8Encoding(false)));

        public void Write(string path, string text) =>
            _work.Add(() =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, text, new UTF8Encoding(false));
            });

        private void Run()
        {
            foreach (var action in _work.GetConsumingEnumerable())
            {
                try { action(); }
                catch (Exception e) { Mod.Log.Warn($"write failed: {e.Message}"); }
            }
        }

        public void Dispose()
        {
            _work.CompleteAdding();
            _thread.Join(2000);
        }
    }
}
