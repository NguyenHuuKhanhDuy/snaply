using System.Runtime.InteropServices;
using System.Text;
using Snaply.Core.Common;
using Snaply.Core.Interfaces;
using Snaply.Core.Models;

namespace Snaply.Platform.Windows;

public sealed class WindowsClipboardService : IClipboardService
{
    public Task<Result> SetImageAsync(RawImage image, CancellationToken ct = default) =>
        Task.Run(() => SetImage(image), ct);

    public Task<Result> SetTextAsync(string text, CancellationToken ct = default) =>
        Task.Run(() => SetText(text), ct);

    private static Result SetImage(RawImage image)
    {
        // CF_DIB: BITMAPINFOHEADER followed by bottom-up 32bpp pixel rows.
        var header = new Native.BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<Native.BITMAPINFOHEADER>(), biWidth = image.Width, biHeight = image.Height,
            biPlanes = 1, biBitCount = 32, biCompression = Native.BI_RGB, biSizeImage = (uint)image.Bgra.Length,
        };
        var headerSize = (int)header.biSize;
        var total = (UIntPtr)(headerSize + image.Bgra.Length);

        var mem = Native.GlobalAlloc(Native.GMEM_MOVEABLE, total);
        if (mem == IntPtr.Zero) return Result.Fail(ErrorCode.Failed, "Not enough memory to copy the image.");

        var ptr = Native.GlobalLock(mem);
        if (ptr == IntPtr.Zero) { Native.GlobalFree(mem); return Result.Fail(ErrorCode.Failed, "Could not prepare the clipboard data."); }
        try
        {
            Marshal.StructureToPtr(header, ptr, false);
            var stride = image.Stride;
            for (var row = 0; row < image.Height; row++)
                Marshal.Copy(image.Bgra, (image.Height - 1 - row) * stride, ptr + headerSize + row * stride, stride);
        }
        finally { Native.GlobalUnlock(mem); }

        return PutOnClipboard(Native.CF_DIB, mem);
    }

    private static Result SetText(string text)
    {
        var bytes = Encoding.Unicode.GetBytes(text + "\0");
        var mem = Native.GlobalAlloc(Native.GMEM_MOVEABLE, (UIntPtr)bytes.Length);
        if (mem == IntPtr.Zero) return Result.Fail(ErrorCode.Failed, "Not enough memory to copy the text.");
        var ptr = Native.GlobalLock(mem);
        if (ptr == IntPtr.Zero) { Native.GlobalFree(mem); return Result.Fail(ErrorCode.Failed, "Could not prepare the clipboard data."); }
        try { Marshal.Copy(bytes, 0, ptr, bytes.Length); }
        finally { Native.GlobalUnlock(mem); }
        return PutOnClipboard(Native.CF_UNICODETEXT, mem);
    }

    private static Result PutOnClipboard(uint format, IntPtr mem)
    {
        // Another program may hold the clipboard briefly; retry before giving up.
        var opened = false;
        for (var i = 0; i < 10 && !opened; i++)
        {
            opened = Native.OpenClipboard(IntPtr.Zero);
            if (!opened) Thread.Sleep(30);
        }
        if (!opened)
        {
            Native.GlobalFree(mem);
            return Result.Fail(ErrorCode.Failed, "The clipboard is in use by another program. Try again.");
        }

        try
        {
            Native.EmptyClipboard();
            if (Native.SetClipboardData(format, mem) == IntPtr.Zero)
            {
                Native.GlobalFree(mem);
                return Result.Fail(ErrorCode.Failed, "Windows refused the clipboard data.");
            }
            return Result.Ok(); // the system now owns the memory
        }
        finally { Native.CloseClipboard(); }
    }
}
