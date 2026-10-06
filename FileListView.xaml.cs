// Copyright © 2020 Paddy Xu, Frank Becker
// This file remains available under the GNU General Public License v3 or later.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Input;

namespace QuickLook.Plugin.FolderViewer
{
    public partial class FileListView : UserControl, IDisposable
    {
        private CancellationToken _cancellationToken;
        private bool _disposed;
        private Func<FileEntry, CancellationToken, Task<IReadOnlyList<FileEntry>>> _loadChildren;

        public FileListView()
        {
            InitializeComponent();
        }

        public void Configure(
            Func<FileEntry, CancellationToken, Task<IReadOnlyList<FileEntry>>> loadChildren,
            CancellationToken cancellationToken)
        {
            _loadChildren = loadChildren ?? throw new ArgumentNullException(nameof(loadChildren));
            _cancellationToken = cancellationToken;
        }

        public void SetItems(IReadOnlyList<FileEntry> entries)
        {
            treeGrid.DataContext = entries ?? Array.Empty<FileEntry>();
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _loadChildren = null;
            treeGrid.DataContext = null;
            GC.SuppressFinalize(this);
        }

        private async void OnItemExpanded(object sender, System.Windows.RoutedEventArgs args)
        {
            if (_disposed || _loadChildren == null || !(sender is TreeViewItem item) ||
                !(item.DataContext is FileEntry entry) || !entry.TryBeginLoading())
            {
                return;
            }

            try
            {
                var children = await _loadChildren(entry, _cancellationToken);
                if (!_disposed && !_cancellationToken.IsCancellationRequested)
                    entry.CompleteLoading(children);
            }
            catch (OperationCanceledException)
            {
                if (!_disposed)
                    entry.FailLoading(Strings.Get("LoadingCanceled"));
            }
            catch (Exception exception)
            {
                if (!_disposed)
                    entry.FailLoading(exception.Message);
            }
        }

        private void OnItemMouseDoubleClick(object sender, MouseButtonEventArgs args)
        {
            if (_disposed || !(sender is TreeViewItem item) || !item.IsSelected ||
                !(item.DataContext is FileEntry entry) ||
                entry.IsPlaceholder || string.IsNullOrEmpty(entry.FullPath))
            {
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo(entry.FullPath) { UseShellExecute = true });
                args.Handled = true;
            }
            catch (Exception exception) when (
                exception is Win32Exception ||
                exception is InvalidOperationException)
            {
                // The file may have disappeared after the preview was populated.
            }
        }
    }
}
