// The Everything 1.5 SDK3 wire protocol used here is documented by voidtools.
// The SDK reference implementation is Copyright (C) 2024 David Carpenter and MIT licensed.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace QuickLook.Plugin.FolderViewer
{
    internal sealed class EverythingFolderSizeProvider : IDisposable
    {
        private const int QueryBudgetMilliseconds = 500;
        private static readonly string[] DefaultPipeNames = { "Everything IPC", "Everything IPC (1.5a)" };
        private readonly Dictionary<string, FolderSizeQueryResult> _cache =
            new Dictionary<string, FolderSizeQueryResult>(StringComparer.OrdinalIgnoreCase);
        private readonly string[] _pipeNames;
        private readonly SemaphoreSlim _queryLock = new SemaphoreSlim(1, 1);
        private volatile bool _disposed;

        public EverythingFolderSizeProvider()
        {
            _pipeNames = DefaultPipeNames;
        }

        internal EverythingFolderSizeProvider(params string[] pipeNames)
        {
            if (pipeNames == null || pipeNames.Length == 0)
                throw new ArgumentException("At least one pipe name is required.", nameof(pipeNames));

            _pipeNames = (string[])pipeNames.Clone();
        }

        public async Task<FolderSizeQueryResult> QueryAsync(string path, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(path))
                return FolderSizeQueryResult.Unavailable;

            var queryCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            queryCancellation.CancelAfter(QueryBudgetMilliseconds);
            var lockTaken = false;
            var disposeCancellation = true;
            try
            {
                try
                {
                    await _queryLock.WaitAsync(queryCancellation.Token).ConfigureAwait(false);
                    lockTaken = true;
                }
                catch (OperationCanceledException)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return FolderSizeQueryResult.Unavailable;
                }

                if (_disposed)
                    return FolderSizeQueryResult.Unavailable;
                if (_cache.TryGetValue(path, out var cached))
                    return cached;

                var queryTask = Task.Run(
                    () => QueryCore(path, queryCancellation.Token),
                    CancellationToken.None);
                var completedTask = await Task.WhenAny(
                    queryTask,
                    Task.Delay(Timeout.Infinite, queryCancellation.Token)).ConfigureAwait(false);
                if (completedTask != queryTask)
                {
                    disposeCancellation = false;
                    ObserveAndDispose(queryTask, queryCancellation);
                    cancellationToken.ThrowIfCancellationRequested();
                    return FolderSizeQueryResult.Unavailable;
                }

                FolderSizeQueryResult result;
                try
                {
                    result = await queryTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return FolderSizeQueryResult.Unavailable;
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (result.IsSuccess)
                    _cache[path] = result;
                return result;
            }
            finally
            {
                if (lockTaken)
                    _queryLock.Release();
                if (disposeCancellation)
                    queryCancellation.Dispose();
            }
        }

        public async Task<FolderAggregateQueryResult> QueryStatisticsAsync(
            string path,
            CancellationToken cancellationToken)
        {
            var queryCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            queryCancellation.CancelAfter(QueryBudgetMilliseconds);
            var disposeCancellation = true;
            var queryPath = path;
            var sizeResult = FolderSizeQueryResult.Unavailable;
            var counts = CountQueryResult.Unavailable;
            try
            {
                var resolveTask = Task.Run(
                    () => ResolveAggregateQueryPath(path),
                    CancellationToken.None);
                var resolvedTask = await Task.WhenAny(
                    resolveTask,
                    Task.Delay(Timeout.Infinite, queryCancellation.Token)).ConfigureAwait(false);
                if (resolvedTask != resolveTask)
                {
                    disposeCancellation = false;
                    ObserveAndDispose(resolveTask, queryCancellation);
                    cancellationToken.ThrowIfCancellationRequested();
                    return default(FolderAggregateQueryResult);
                }

                queryPath = await resolveTask.ConfigureAwait(false);
                sizeResult = await QueryAsync(queryPath, queryCancellation.Token).ConfigureAwait(false);
                queryCancellation.Token.ThrowIfCancellationRequested();

                var countTask = Task.Run(() =>
                {
                    var success = EverythingLegacyIpc.TryGetDescendantCounts(
                        queryPath,
                        queryCancellation.Token,
                        out var directoryCount,
                        out var fileCount);
                    return new CountQueryResult(success, directoryCount, fileCount);
                }, CancellationToken.None);
                var completedTask = await Task.WhenAny(
                    countTask,
                    Task.Delay(Timeout.Infinite, queryCancellation.Token)).ConfigureAwait(false);
                if (completedTask != countTask)
                {
                    disposeCancellation = false;
                    ObserveAndDispose(countTask, queryCancellation);
                    cancellationToken.ThrowIfCancellationRequested();
                }
                else
                {
                    counts = await countTask.ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            finally
            {
                if (disposeCancellation)
                    queryCancellation.Dispose();
            }

            return new FolderAggregateQueryResult(
                sizeResult.IsSuccess,
                sizeResult.Size,
                counts.IsSuccess,
                counts.DirectoryCount,
                counts.FileCount,
                sizeResult.Source);
        }

        public void Dispose()
        {
            _disposed = true;
        }

        private FolderSizeQueryResult QueryCore(string path, CancellationToken cancellationToken)
        {
            var queryPath = path;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var retryResolvedPath = false;
                foreach (var pipeName in _pipeNames)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!TryQuerySdk3Pipe(pipeName, queryPath, cancellationToken, out var size))
                        continue;
                    if (size != 0 || !IsReparsePoint(queryPath))
                        return new FolderSizeQueryResult(true, size, "Everything 1.5 index");
                    if (attempt == 0 && TryResolveLocalPath(queryPath, out var resolvedPath))
                    {
                        queryPath = resolvedPath;
                        retryResolvedPath = true;
                        break;
                    }

                    return FolderSizeQueryResult.Unavailable;
                }

                if (retryResolvedPath)
                    continue;

                cancellationToken.ThrowIfCancellationRequested();
                if (EverythingLegacyIpc.TryGetFolderSize(
                    queryPath,
                    cancellationToken,
                    out var legacySize))
                {
                    if (legacySize != 0 || !IsReparsePoint(queryPath))
                        return new FolderSizeQueryResult(true, legacySize, "Everything index");
                    if (attempt == 0 && TryResolveLocalPath(queryPath, out var resolvedLegacyPath))
                    {
                        queryPath = resolvedLegacyPath;
                        continue;
                    }
                }

                return FolderSizeQueryResult.Unavailable;
            }

            return FolderSizeQueryResult.Unavailable;
        }

        private static string ResolveAggregateQueryPath(string path)
        {
            if (DirectoryStatisticsScanner.IsNetworkPath(path) || !IsReparsePoint(path))
                return path;

            return TryResolveLocalPath(path, out var resolvedPath) ? resolvedPath : path;
        }

        internal static bool TryQuerySdk3Pipe(
            string pipeName,
            string path,
            CancellationToken cancellationToken,
            out long size)
        {
            size = 0;
            try
            {
                using (var pipe = new NamedPipeClientStream(
                    ".",
                    pipeName,
                    PipeDirection.InOut,
                    PipeOptions.Asynchronous))
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    timeout.CancelAfter(500);
                    using (timeout.Token.Register(pipe.Dispose))
                    {
                    pipe.Connect(0);
                    var pathBytes = Encoding.UTF8.GetBytes(path);
                    var header = new byte[8];
                    WriteUInt32(header, 0, 18);
                    WriteUInt32(header, 4, checked((uint)pathBytes.Length));
                    pipe.Write(header, 0, header.Length);
                    pipe.Write(pathBytes, 0, pathBytes.Length);
                    pipe.Flush();

                    if (!ReadExactly(pipe, header, 0, header.Length))
                        return FailIfNotCancelled(cancellationToken);

                    var responseCode = ReadUInt32(header, 0);
                    var responseLength = ReadUInt32(header, 4);
                    if ((responseCode != 100 && responseCode != 200) || responseLength != 8)
                        return FailIfNotCancelled(cancellationToken);

                    var payload = new byte[8];
                    if (!ReadExactly(pipe, payload, 0, payload.Length))
                        return FailIfNotCancelled(cancellationToken);

                    var unsignedSize = ReadUInt64(payload, 0);
                    if (unsignedSize == ulong.MaxValue)
                        return false;

                    size = unsignedSize > long.MaxValue ? long.MaxValue : (long)unsignedSize;
                    return true;
                    }
                }
            }
            catch (Exception exception) when (
                exception is IOException ||
                exception is TimeoutException ||
                exception is UnauthorizedAccessException ||
                exception is InvalidOperationException ||
                exception is ObjectDisposedException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return false;
            }
        }

        private static bool IsReparsePoint(string path)
        {
            try
            {
                return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
            }
            catch (Exception exception) when (
                exception is IOException ||
                exception is UnauthorizedAccessException ||
                exception is ArgumentException ||
                exception is NotSupportedException ||
                exception is System.Security.SecurityException)
            {
                return false;
            }
        }

        private static bool TryResolveLocalPath(string path, out string resolvedPath)
        {
            resolvedPath = null;
            if (DirectoryStatisticsScanner.IsNetworkPath(path))
                return false;

            using (var handle = CreateFile(
                path,
                0,
                1 | 2 | 4,
                IntPtr.Zero,
                3,
                0x02000000,
                IntPtr.Zero))
            {
                if (handle.IsInvalid)
                    return false;

                var buffer = new StringBuilder(512);
                var length = GetFinalPathNameByHandle(handle, buffer, buffer.Capacity, 0);
                if (length == 0)
                    return false;
                if (length >= buffer.Capacity)
                {
                    buffer = new StringBuilder(checked((int)length + 1));
                    length = GetFinalPathNameByHandle(handle, buffer, buffer.Capacity, 0);
                    if (length == 0 || length >= buffer.Capacity)
                        return false;
                }

                var value = buffer.ToString();
                if (value.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase))
                    value = "\\\\" + value.Substring(8);
                else if (value.StartsWith("\\\\?\\", StringComparison.OrdinalIgnoreCase))
                    value = value.Substring(4);

                if (string.Equals(value, path, StringComparison.OrdinalIgnoreCase))
                    return false;

                resolvedPath = value;
                return true;
            }
        }

        private static bool ReadExactly(Stream stream, byte[] buffer, int offset, int count)
        {
            while (count > 0)
            {
                var read = stream.Read(buffer, offset, count);
                if (read == 0)
                    return false;
                offset += read;
                count -= read;
            }

            return true;
        }

        private static bool FailIfNotCancelled(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }

        private static void ObserveAndDispose(Task task, CancellationTokenSource cancellation)
        {
            task.ContinueWith(
                completedTask =>
                {
                    var ignored = completedTask.Exception;
                    cancellation.Dispose();
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private static uint ReadUInt32(byte[] buffer, int offset)
        {
            return (uint)(buffer[offset] |
                buffer[offset + 1] << 8 |
                buffer[offset + 2] << 16 |
                buffer[offset + 3] << 24);
        }

        private static ulong ReadUInt64(byte[] buffer, int offset)
        {
            return ReadUInt32(buffer, offset) | ((ulong)ReadUInt32(buffer, offset + 4) << 32);
        }

        private static void WriteUInt32(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
            buffer[offset + 2] = (byte)(value >> 16);
            buffer[offset + 3] = (byte)(value >> 24);
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(
            SafeFileHandle file,
            StringBuilder filePath,
            int filePathSize,
            uint flags);

        private readonly struct CountQueryResult
        {
            public static CountQueryResult Unavailable => new CountQueryResult(false, 0, 0);

            public CountQueryResult(bool isSuccess, long directoryCount, long fileCount)
            {
                IsSuccess = isSuccess;
                DirectoryCount = directoryCount;
                FileCount = fileCount;
            }

            public bool IsSuccess { get; }
            public long DirectoryCount { get; }
            public long FileCount { get; }
        }
    }

    internal readonly struct FolderSizeQueryResult
    {
        public static FolderSizeQueryResult Unavailable => new FolderSizeQueryResult(false, 0, null);

        public FolderSizeQueryResult(bool isSuccess, long size, string source)
        {
            IsSuccess = isSuccess;
            Size = size;
            Source = source;
        }

        public bool IsSuccess { get; }
        public long Size { get; }
        public string Source { get; }
    }

    internal readonly struct FolderAggregateQueryResult
    {
        public FolderAggregateQueryResult(
            bool hasSize,
            long size,
            bool hasCounts,
            long directoryCount,
            long fileCount,
            string source)
        {
            HasSize = hasSize;
            Size = size;
            HasCounts = hasCounts;
            DirectoryCount = directoryCount;
            FileCount = fileCount;
            Source = source;
        }

        public bool HasSize { get; }
        public long Size { get; }
        public bool HasCounts { get; }
        public long DirectoryCount { get; }
        public long FileCount { get; }
        public string Source { get; }
    }
}
