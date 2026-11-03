using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Pitstop.Core;

namespace Pitstop.Windows;

public sealed class AppIconConverter : IValueConverter
{
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);
    public object? Convert(object value, Type type, object parameter, CultureInfo culture)
    {
        var path = value is AppRule rule
            ? rule.LaunchTarget.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? rule.LaunchTarget : rule.ProcessPath
            : value as string;
        if (string.IsNullOrWhiteSpace(path)) return null;
        path = WindowsProcessHost.Expand(path);
        if (Cache.TryGetValue(path, out var cached)) return cached;
        ImageSource? result = null;
        if (File.Exists(path))
        {
            var info = new ShellFileInfo();
            var found = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<ShellFileInfo>(), 0x100);
            try
            {
                if (found != IntPtr.Zero && info.Icon != IntPtr.Zero)
                {
                    var bitmap = Imaging.CreateBitmapSourceFromHIcon(info.Icon, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(32, 32));
                    bitmap.Freeze(); result = bitmap;
                }
            }
            finally { if (info.Icon != IntPtr.Zero) DestroyIcon(info.Icon); }
        }
        Cache[path] = result;
        return result;
    }
    public object ConvertBack(object value, Type type, object parameter, CultureInfo culture) => Binding.DoNothing;
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr Icon;
        public int Index;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref ShellFileInfo info, uint size, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
}
