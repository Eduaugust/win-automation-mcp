using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace WinAutomationMcp;

static class Capture
{
    public static byte[] Encode(Bitmap bmp, string format, int quality)
    {
        using var ms = new MemoryStream();
        if (format == "jpeg")
        {
            var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
            using var ep = new EncoderParameters(1);
            ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)Math.Clamp(quality, 1, 100));
            bmp.Save(ms, codec, ep);
        }
        else bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    static Bitmap CopyScreen(Rectangle r)
    {
        var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(r.Location, Point.Empty, r.Size);
        return bmp;
    }

    public static ToolResult Screenshot(Args a)
    {
        var windowMatch = a.Str("window");
        var format = (a.Str("format") ?? "png").ToLowerInvariant();
        if (format == "jpg") format = "jpeg";
        if (format != "png" && format != "jpeg") return "ERROR: format must be png or jpeg";
        int quality = a.Int("quality") ?? 80;
        int maxWidth = a.Int("max_width") ?? 1280;
        var saveTo = a.Str("save_to");
        bool includePopups = a.Bool("include_popups") ?? true;
        bool bringToFront = a.Bool("bring_to_front") ?? true;
        bool asText = a.Bool("as_text") ?? false;
        bool returnImage = a.Bool("return_image") ?? true;
        var method = (a.Str("method") ?? "auto").ToLowerInvariant();

        Rectangle winRect;        // origin for region_* (window visible frame, or virtual screen)
        Rectangle capRect;        // what is actually grabbed (window + popups)
        IntPtr hwnd = IntPtr.Zero;
        string label;
        int popups = 0;

        if (!string.IsNullOrWhiteSpace(windowMatch))
        {
            var (win, h, err) = Uia.ResolveWindow(windowMatch);
            if (err != null) return err;
            hwnd = h;
            if (hwnd == IntPtr.Zero) return $"ERROR: window \"{windowMatch}\" has no native handle";
            bool needScreen = includePopups || method == "screen";
            if (Native.IsIconic(hwnd) || (bringToFront && needScreen))
            {
                if (!bringToFront && Native.IsIconic(hwnd)) return $"ERROR: window \"{windowMatch}\" is minimized; pass bring_to_front=true to restore it";
                Native.Focus(hwnd);
                Thread.Sleep(120);
            }
            winRect = Native.VisibleRect(hwnd);
            capRect = winRect;
            if (includePopups)
            {
                foreach (var pr in Native.PopupRects(hwnd)) { capRect = Rectangle.Union(capRect, pr); popups++; }
                capRect = Rectangle.Intersect(capRect, Native.VirtualScreen());
            }
            label = windowMatch;
        }
        else
        {
            winRect = Native.VirtualScreen();
            capRect = winRect;
            label = "(full screen)";
        }

        if (capRect.Width <= 0 || capRect.Height <= 0)
            return $"ERROR: window \"{label}\" has invalid bounds {capRect.Width}x{capRect.Height} (minimized or off-screen?)";

        Bitmap? bmp = null;
        string used;
        bool wantPrint = hwnd != IntPtr.Zero && !includePopups && method != "screen";
        if (wantPrint)
        {
            var outer = Native.OuterRect(hwnd);
            var pb = new Bitmap(Math.Max(1, outer.Width), Math.Max(1, outer.Height), PixelFormat.Format32bppArgb);
            bool ok;
            using (var g = Graphics.FromImage(pb))
            {
                IntPtr hdc = g.GetHdc();
                try { ok = Native.PrintWindow(hwnd, hdc, Native.PW_RENDERFULLCONTENT); }
                finally { g.ReleaseHdc(hdc); }
            }
            if (ok && method != "screen")
            {
                var crop = new Rectangle(winRect.X - outer.X, winRect.Y - outer.Y, winRect.Width, winRect.Height);
                crop.Intersect(new Rectangle(0, 0, pb.Width, pb.Height));
                bmp = crop.Width > 0 && crop.Height > 0 ? pb.Clone(crop, pb.PixelFormat) : (Bitmap)pb.Clone();
                pb.Dispose();
                used = "PrintWindow (no popups, window may be occluded-safe)";
            }
            else { pb.Dispose(); used = ""; }
        }
        else used = "";
        if (bmp == null)
        {
            bmp = CopyScreen(capRect);
            used = hwnd == IntPtr.Zero ? "CopyFromScreen (virtual screen)"
                 : $"CopyFromScreen of window rect{(popups > 0 ? $" + {popups} popup window(s)" : "")}{(bringToFront ? "" : " (not brought to front, may show occluding windows)")}";
        }

        using (bmp)
        {
            // region_* are relative to the top-left of winRect; translate into the captured bitmap
            Bitmap work = bmp;
            string regionNote = "none";
            Point origin = capRect.Location;
            if (a.Int("region_x").HasValue && a.Int("region_y").HasValue && a.Int("region_w").HasValue && a.Int("region_h").HasValue)
            {
                var req = new Rectangle(a.Int("region_x")!.Value + winRect.X - capRect.X, a.Int("region_y")!.Value + winRect.Y - capRect.Y,
                                        a.Int("region_w")!.Value, a.Int("region_h")!.Value);
                var clamped = Rectangle.Intersect(new Rectangle(0, 0, bmp.Width, bmp.Height), req);
                if (clamped.Width <= 0 || clamped.Height <= 0)
                    return $"ERROR: region ({a.Int("region_x")},{a.Int("region_y")},{a.Int("region_w")}x{a.Int("region_h")}), relative to the {(hwnd != IntPtr.Zero ? "window" : "virtual screen")} top-left, does not overlap the captured image ({winRect.Width}x{winRect.Height} at screen {winRect.X},{winRect.Y})";
                work = bmp.Clone(clamped, bmp.PixelFormat);
                origin = new Point(capRect.X + clamped.X, capRect.Y + clamped.Y);
                regionNote = $"{clamped.X + capRect.X - winRect.X},{clamped.Y + capRect.Y - winRect.Y},{clamped.Width}x{clamped.Height}";
            }

            try
            {
                int fullW = work.Width, fullH = work.Height;

                // Optional file on the VM: full resolution, format from extension if recognised
                string? savedInfo = null;
                if (!string.IsNullOrWhiteSpace(saveTo))
                {
                    var ext = Path.GetExtension(saveTo).ToLowerInvariant();
                    var sf = ext == ".jpg" || ext == ".jpeg" ? "jpeg" : ext == ".png" ? "png" : format;
                    var full = Encode(work, sf, quality);
                    var dir = Path.GetDirectoryName(Path.GetFullPath(saveTo));
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    File.WriteAllBytes(saveTo, full);
                    savedInfo = $"saved {fullW}x{fullH}px {sf} ({full.Length} bytes) to {Path.GetFullPath(saveTo)}";
                }

                Bitmap outBmp = work;
                double scale = 1.0;
                if (maxWidth > 0 && work.Width > maxWidth)
                {
                    scale = (double)maxWidth / work.Width;
                    int nh = Math.Max(1, (int)Math.Round(work.Height * scale));
                    outBmp = new Bitmap(maxWidth, nh, PixelFormat.Format24bppRgb);
                    using var g = Graphics.FromImage(outBmp);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.SmoothingMode = SmoothingMode.HighQuality;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(work, new Rectangle(0, 0, maxWidth, nh), new Rectangle(0, 0, work.Width, work.Height), GraphicsUnit.Pixel);
                }

                try
                {
                    var data = returnImage || asText ? Encode(outBmp, format, quality) : Array.Empty<byte>();
                    var info = new StringBuilder();
                    info.Append($"OK: screenshot of \"{label}\": captured {fullW}x{fullH}px");
                    if (scale < 1.0) info.Append($", returned {outBmp.Width}x{outBmp.Height}px (scale {scale:0.####}, max_width={maxWidth})");
                    if (returnImage || asText) info.Append($", {data.Length} bytes {format}");
                    info.Append($"; method={used}; region(relative to {(hwnd != IntPtr.Zero ? "window" : "virtual screen")})={regionNote}; image top-left is at screen ({origin.X},{origin.Y}); screen_x = {origin.X} + pixel_x / {scale:0.####}");
                    if (savedInfo != null) info.Append("; " + savedInfo);

                    if (asText) return info + "\n" + Convert.ToBase64String(data);
                    var res = new ToolResult().AddText(info.ToString());
                    if (returnImage) res.AddImage(data, format == "jpeg" ? "image/jpeg" : "image/png");
                    return res;
                }
                finally { if (!ReferenceEquals(outBmp, work)) outBmp.Dispose(); }
            }
            finally { if (!ReferenceEquals(work, bmp)) work.Dispose(); }
        }
    }
}
