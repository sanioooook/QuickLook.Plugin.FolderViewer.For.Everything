using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QuickLook.Plugin.FolderViewer
{
    internal static class SystemIconProvider
    {
        private const int MaximumCachedIcons = 512;
        private const int MaximumExtensionLength = 24;
        private const uint FileAttributeDirectory = 0x10;
        private const uint FileAttributeNormal = 0x80;
        private const uint ShgfiIcon = 0x000000100;
        private const uint ShgfiSmallIcon = 0x000000001;
        private const uint ShgfiUseFileAttributes = 0x000000010;

        private static readonly Dictionary<string, ImageSource> Cache =
            new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);
        private static readonly object CacheLock = new object();

        public static ImageSource GetIcon(string fullPath, bool isFolder)
        {
            var key = isFolder ? "<folder>" : GetExtensionKey(fullPath);
            lock (CacheLock)
            {
                if (Cache.TryGetValue(key, out var cached))
                    return cached;
                if (Cache.Count >= MaximumCachedIcons)
                    key = isFolder ? "<folder>" : "<file>";
                if (Cache.TryGetValue(key, out cached))
                    return cached;

                var icon = LoadIcon(key, isFolder);
                Cache[key] = icon;
                return icon;
            }
        }

        private static string GetExtensionKey(string path)
        {
            try
            {
                var extension = Path.GetExtension(path);
                return string.IsNullOrEmpty(extension) || extension.Length > MaximumExtensionLength
                    ? "<file>"
                    : extension;
            }
            catch (ArgumentException)
            {
                return "<file>";
            }
        }

        private static ImageSource LoadIcon(string key, bool isFolder)
        {
            var attributes = isFolder ? FileAttributeDirectory : FileAttributeNormal;
            var lookupName = isFolder ? "folder" : "file" + (key.StartsWith("<", StringComparison.Ordinal) ? string.Empty : key);
            SHFILEINFO info;
            var result = SHGetFileInfo(
                lookupName,
                attributes,
                out info,
                (uint)Marshal.SizeOf(typeof(SHFILEINFO)),
                ShgfiIcon | ShgfiSmallIcon | ShgfiUseFileAttributes);

            if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero)
                return null;

            try
            {
                var source = Imaging.CreateBitmapSourceFromHIcon(
                    info.hIcon,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromWidthAndHeight(16, 16));
                source.Freeze();
                return source;
            }
            catch (Exception exception) when (
                exception is ArgumentException ||
                exception is ExternalException)
            {
                return null;
            }
            finally
            {
                DestroyIcon(info.hIcon);
            }
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHGetFileInfo(
            string pszPath,
            uint dwFileAttributes,
            out SHFILEINFO psfi,
            uint cbFileInfo,
            uint uFlags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }
    }
}
