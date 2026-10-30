using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IslandEye
{
    /// <summary>Looks up an app's display name and icon from its AppUserModelID (the id Windows toasts carry).</summary>
    internal static class ShellApps
    {
        [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }

        [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            [PreserveSig] int GetParent(out IShellItem ppsi);
            [PreserveSig] int GetDisplayName(uint sigdnName, out IntPtr ppszName);
            [PreserveSig] int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
            [PreserveSig] int Compare(IShellItem psi, uint hint, out int piOrder);
        }

        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemImageFactory
        {
            [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out object ppv);

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public short bmPlanes, bmBitsPixel; public IntPtr bmBits; }
        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount;
            public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct DIBSECTION
        {
            public BITMAP dsBm; public BITMAPINFOHEADER dsBmih;
            public uint dsBitfields0, dsBitfields1, dsBitfields2; public IntPtr dshSection; public uint dsOffset;
        }

        [DllImport("gdi32.dll")] private static extern int GetObject(IntPtr h, int cb, out DIBSECTION ds);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);

        private static readonly Dictionary<string, (string? Name, ImageSource? Icon)> Cache = new();

        /// <summary>Call on the UI thread. Returns (null, null) when the app can't be resolved.</summary>
        public static (string? Name, ImageSource? Icon) Resolve(string aumid)
        {
            if (string.IsNullOrWhiteSpace(aumid)) return (null, null);
            if (Cache.TryGetValue(aumid, out var cached)) return cached;

            string? name = null;
            ImageSource? icon = null;
            try
            {
                Guid iid = typeof(IShellItem).GUID;
                int hr = SHCreateItemFromParsingName(@"shell:AppsFolder\" + aumid, IntPtr.Zero, ref iid, out object obj);
                if (hr == 0 && obj != null)
                {
                    if (obj is IShellItem item && item.GetDisplayName(0, out IntPtr p) == 0 && p != IntPtr.Zero)
                    {
                        name = Marshal.PtrToStringUni(p);
                        Marshal.FreeCoTaskMem(p);
                    }
                    if (obj is IShellItemImageFactory factory)
                    {
                        var size = new SIZE { cx = 64, cy = 64 };
                        if (factory.GetImage(size, 0x1 /* BIGGERSIZEOK */, out IntPtr hbm) == 0 && hbm != IntPtr.Zero)
                        {
                            try { icon = FromHBitmap(hbm); } finally { DeleteObject(hbm); }
                        }
                    }
                }
            }
            catch { }

            var result = (name, icon);
            Cache[aumid] = result;
            return result;
        }

        private static ImageSource? FromHBitmap(IntPtr hbm)
        {
            try
            {
                if (GetObject(hbm, Marshal.SizeOf<DIBSECTION>(), out DIBSECTION ds) != 0 &&
                    ds.dsBm.bmBits != IntPtr.Zero && ds.dsBm.bmBitsPixel == 32)
                {
                    int w = ds.dsBm.bmWidth, h = ds.dsBm.bmHeight, stride = ds.dsBm.bmWidthBytes;
                    var data = new byte[stride * h];
                    bool bottomUp = ds.dsBmih.biHeight > 0;
                    for (int y = 0; y < h; y++)
                    {
                        int srcRow = bottomUp ? h - 1 - y : y;
                        Marshal.Copy(IntPtr.Add(ds.dsBm.bmBits, srcRow * stride), data, y * stride, stride);
                    }
                    bool anyAlpha = false;
                    for (int i = 3; i < data.Length; i += 4) if (data[i] != 0) { anyAlpha = true; break; }
                    if (!anyAlpha) for (int i = 3; i < data.Length; i += 4) data[i] = 255;

                    var bs = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, data, stride);
                    bs.Freeze();
                    return bs;
                }
                var fallback = Imaging.CreateBitmapSourceFromHBitmap(hbm, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                fallback.Freeze();
                return fallback;
            }
            catch { return null; }
        }
    }
}
