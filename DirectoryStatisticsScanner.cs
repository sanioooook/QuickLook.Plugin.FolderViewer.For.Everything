using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace QuickLook.Plugin.FolderViewer
{
    internal static class DirectoryStatisticsScanner
    {
        private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(300);

        public static DirectoryStatistics Scan(
            string rootPath,
            CancellationToken cancellationToken,
            Action<DirectoryStatistics> progress = null)
        {
            var pending = new Stack<string>();
            var statistics = new MutableStatistics();
            var stopwatch = Stopwatch.StartNew();
            var entriesUntilProgressCheck = 256;
            pending.Push(rootPath);

            while (pending.Count != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = pending.Pop();

                var outcome = DirectoryEnumerator.Enumerate(
                    path,
                    cancellationToken,
                    int.MaxValue,
                    item =>
                    {
                        if (item.IsDirectory)
                        {
                            statistics.DirectoryCount++;
                            if (!item.IsReparsePoint)
                                pending.Push(item.FullPath);
                        }
                        else
                        {
                            statistics.FileCount++;
                            statistics.TotalSize = SaturatingAdd(statistics.TotalSize, item.Size);
                        }

                        if (progress != null && --entriesUntilProgressCheck == 0)
                        {
                            entriesUntilProgressCheck = 256;
                            if (stopwatch.Elapsed >= ProgressInterval)
                            {
                                progress(statistics.Snapshot(false));
                                stopwatch.Restart();
                            }
                        }

                        return true;
                    });

                if (outcome.ErrorCode != 0)
                    statistics.InaccessibleDirectoryCount++;
            }

            var result = statistics.Snapshot(true);
            progress?.Invoke(result);
            return result;
        }

        public static bool IsNetworkPath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return false;
            if (path.StartsWith("\\\\", StringComparison.Ordinal))
                return true;

            var root = System.IO.Path.GetPathRoot(path);
            return !string.IsNullOrEmpty(root) && GetDriveType(root) == DriveRemote;
        }

        private static long SaturatingAdd(long current, long value)
        {
            if (value <= 0)
                return current;
            return current > long.MaxValue - value ? long.MaxValue : current + value;
        }

        private const uint DriveRemote = 4;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern uint GetDriveType(string lpRootPathName);

        private sealed class MutableStatistics
        {
            public long DirectoryCount;
            public long FileCount;
            public int InaccessibleDirectoryCount;
            public long TotalSize;

            public DirectoryStatistics Snapshot(bool isComplete)
            {
                return new DirectoryStatistics(
                    DirectoryCount,
                    FileCount,
                    TotalSize,
                    InaccessibleDirectoryCount,
                    isComplete);
            }
        }
    }

    internal readonly struct DirectoryStatistics
    {
        public DirectoryStatistics(
            long directoryCount,
            long fileCount,
            long totalSize,
            int inaccessibleDirectoryCount,
            bool isComplete)
        {
            DirectoryCount = directoryCount;
            FileCount = fileCount;
            TotalSize = totalSize;
            InaccessibleDirectoryCount = inaccessibleDirectoryCount;
            IsComplete = isComplete;
        }

        public long DirectoryCount { get; }
        public long FileCount { get; }
        public long TotalSize { get; }
        public int InaccessibleDirectoryCount { get; }
        public bool IsComplete { get; }
    }
}
