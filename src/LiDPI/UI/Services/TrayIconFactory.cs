using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using LiDPI.Core;

namespace LiDPI.UI.Services;

/// <summary>Draws the state-coloured shield used for the tray icon (no icon handles leaked).</summary>
internal static class TrayIconFactory
{
    private static readonly Color Mint = Color.FromArgb(0x2E, 0xE6, 0xA6);
    private static readonly Color Cyan = Color.FromArgb(0x22, 0xC3, 0xE6);
    private static readonly Color Amber = Color.FromArgb(0xF5, 0xB5, 0x44);
    private static readonly Color Red = Color.FromArgb(0xFF, 0x5C, 0x7A);
    private static readonly Color Idle = Color.FromArgb(0x7A, 0x7A, 0x8A);

    public static Icon Create(EngineState state)
    {
        int[] sizes = [16, 20, 24, 32, 48];
        var pngs = sizes.Select(s => RenderPng(s, state)).ToArray();

        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)sizes.Length);
            var offset = 6 + 16 * sizes.Length;
            for (var i = 0; i < sizes.Length; i++)
            {
                w.Write((byte)sizes[i]); w.Write((byte)sizes[i]); w.Write((byte)0); w.Write((byte)0);
                w.Write((ushort)1); w.Write((ushort)32);
                w.Write((uint)pngs[i].Length); w.Write((uint)offset);
                offset += pngs[i].Length;
            }
            foreach (var p in pngs) w.Write(p);
        }
        ms.Position = 0;
        return new Icon(ms, System.Windows.Forms.SystemInformation.SmallIconSize);
    }

    private static byte[] RenderPng(int size, EngineState state)
    {
        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);

            // the shield spans x 4..20, y 2..22 of its 24-unit design box: scale it to fill the icon
            var k = size * 0.98f / 20f;
            var dx = (size - 16 * k) / 2f - 4 * k;
            var dy = (size - 20 * k) / 2f - 2 * k;
            using var shield = Shield(k);
            using (var m = new Matrix()) { m.Translate(dx, dy); shield.Transform(m); }

            using Brush fill = state switch
            {
                EngineState.Running => new LinearGradientBrush(new RectangleF(0, 0, size, size), Mint, Cyan, 45f),
                EngineState.Starting or EngineState.Stopping => new SolidBrush(Amber),
                EngineState.Error => new SolidBrush(Red),
                _ => new SolidBrush(Idle),
            };
            g.FillPath(fill, shield);

            // small dark core dot gives the glyph some depth, readable on light and dark taskbars
            var d = size * 0.30f;
            using var core = new SolidBrush(Color.FromArgb(state == EngineState.Stopped ? 200 : 230, 0x0A, 0x0A, 0x0E));
            g.FillEllipse(core, (size - d) / 2f, dy + 10.5f * k - d / 2f, d, d);
        }
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    /// <summary>Shield outline (same shape as the app icon), 24-unit design box scaled by <paramref name="k"/>.</summary>
    private static GraphicsPath Shield(float k)
    {
        var p = new GraphicsPath();
        p.AddLine(12 * k, 2 * k, 20 * k, 5 * k);
        p.AddLine(20 * k, 5 * k, 20 * k, 11 * k);
        p.AddBezier(20 * k, 11 * k, 20 * k, 16.5f * k, 16.6f * k, 20.6f * k, 12 * k, 22 * k);
        p.AddBezier(12 * k, 22 * k, 7.4f * k, 20.6f * k, 4 * k, 16.5f * k, 4 * k, 11 * k);
        p.AddLine(4 * k, 11 * k, 4 * k, 5 * k);
        p.CloseFigure();
        return p;
    }
}
