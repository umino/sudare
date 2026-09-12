using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IconGen;

/// <summary>
/// Sudare の新アプリアイコン「円窓と光簾（まるまどとこうれん）」を描画し、
/// Windows ネイティブアプリ用の .ico および各解像度プレビュー PNG を書き出す。
/// </summary>
internal static class Program
{
    /// <summary>ICO に格納する標準解像度セット（256 のみ PNG 圧縮、他は 32-bit BMP）。</summary>
    private static readonly int[] Sizes = { 16, 24, 32, 48, 64, 128, 256 };

    private static int Main(string[] args)
    {
        string outDir = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();
        Directory.CreateDirectory(outDir);

        var frames = new List<(int Size, byte[] Data, bool Png)>();
        foreach (int size in Sizes)
        {
            var bitmap = Render(size);
            bool png = size >= 256;
            frames.Add((size, png ? EncodePng(bitmap) : EncodeBmp(bitmap), png));

            // 全サイズ確認用プレビュー PNG
            File.WriteAllBytes(Path.Combine(outDir, $"preview_{size}.png"), EncodePng(bitmap));
        }

        string icoPath = Path.Combine(outDir, "Sudare.ico");
        File.WriteAllBytes(icoPath, BuildIco(frames));

        var info = new FileInfo(icoPath);
        Console.WriteLine($"Generated: {icoPath} ({info.Length:N0} bytes, {frames.Count} frames)");
        foreach (var (size, data, png) in frames)
        {
            Console.WriteLine($"  {size,3}x{size,-3} {(png ? "PNG" : "BMP")}  {data.Length,8:N0} bytes");
        }
        return 0;
    }

    #region 描画

    private static RenderTargetBitmap Render(int px)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            Draw(dc, px);
        }

        var bitmap = new RenderTargetBitmap(px, px, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static void Draw(DrawingContext dc, int size)
    {
        double center = size / 2.0;

        // 1. 円窓のベゼル（外枠）
        double outerR = size switch
        {
            16 => 7.25,
            24 => 11.0,
            32 => 14.5,
            _ => size * (114.0 / 256.0)
        };

        double innerR = size switch
        {
            16 => 5.75,
            24 => 9.0,
            32 => 12.0,
            _ => size * (96.0 / 256.0)
        };

        // ベゼルグラデーション
        var rimGrad = new LinearGradientBrush(
            Color.FromRgb(0x4B, 0x5E, 0x78),
            Color.FromRgb(0x14, 0x1C, 0x28),
            45)
        {
            MappingMode = BrushMappingMode.RelativeToBoundingBox
        };
        rimGrad.GradientStops.Add(new GradientStop(Color.FromRgb(0x24, 0x31, 0x44), 0.5));
        rimGrad.Freeze();

        double rimStrokeWidth = size switch
        {
            16 => 0.75,
            24 => 1.0,
            32 => 1.2,
            _ => Math.Max(1.5, size * (2.5 / 256.0))
        };

        var rimStrokePen = new Pen(new SolidColorBrush(Color.FromRgb(0x60, 0x76, 0x94)), rimStrokeWidth);
        rimStrokePen.Freeze();

        // 外枠を描画
        dc.DrawEllipse(rimGrad, rimStrokePen, new Point(center, center), outerR, outerR);

        // 2. 円窓の内側（空間）
        var innerVoidBrush = new SolidColorBrush(Color.FromRgb(0x0C, 0x13, 0x1D));
        innerVoidBrush.Freeze();
        dc.DrawEllipse(innerVoidBrush, null, new Point(center, center), innerR, innerR);

        // 窓内部をクリッピングして描画
        var clipGeo = new EllipseGeometry(new Point(center, center), innerR, innerR);
        clipGeo.Freeze();
        dc.PushClip(clipGeo);

        // A. 斜光帯（左上から右下へ差し込む光）
        var lightBrush = new LinearGradientBrush
        {
            StartPoint = new Point(size * 0.15, size * 0.05),
            EndPoint = new Point(size * 0.85, size * 0.95),
            MappingMode = BrushMappingMode.Absolute
        };
        lightBrush.GradientStops.Add(new GradientStop(Color.FromArgb(size <= 32 ? (byte)0x40 : (byte)0x38, 0xFF, 0xF9, 0xC4), 0.0));
        lightBrush.GradientStops.Add(new GradientStop(Color.FromArgb(size <= 32 ? (byte)0x30 : (byte)0x2D, 0xC8, 0xE6, 0xC9), 0.5));
        lightBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0x1B, 0x2A, 0x3A), 1.0));
        lightBrush.Freeze();

        var lightPath = new StreamGeometry();
        using (var ctx = lightPath.Open())
        {
            if (size == 16)
            {
                ctx.BeginFigure(new Point(4, 2), true, true);
                ctx.LineTo(new Point(9, 2), true, false);
                ctx.LineTo(new Point(12, 14), true, false);
                ctx.LineTo(new Point(7, 14), true, false);
            }
            else if (size == 24)
            {
                ctx.BeginFigure(new Point(5, 3), true, true);
                ctx.LineTo(new Point(14, 3), true, false);
                ctx.LineTo(new Point(19, 21), true, false);
                ctx.LineTo(new Point(10, 21), true, false);
            }
            else if (size == 32)
            {
                ctx.BeginFigure(new Point(7, 4), true, true);
                ctx.LineTo(new Point(19, 4), true, false);
                ctx.LineTo(new Point(25, 28), true, false);
                ctx.LineTo(new Point(13, 28), true, false);
            }
            else
            {
                double s = size / 256.0;
                ctx.BeginFigure(new Point(50 * s, 10 * s), true, true);
                ctx.LineTo(new Point(170 * s, 10 * s), true, false);
                ctx.LineTo(new Point(240 * s, 240 * s), true, false);
                ctx.LineTo(new Point(120 * s, 240 * s), true, false);
            }
        }
        lightPath.Freeze();
        dc.DrawGeometry(lightBrush, null, lightPath);

        // B. 簾の編み糸（48px以上のみ描画）
        if (size >= 48)
        {
            double s = size / 256.0;
            var threadPen = new Pen(new SolidColorBrush(Color.FromRgb(0x2D, 0x3B, 0x4F)), Math.Max(1.0, 2.5 * s));
            threadPen.Freeze();
            dc.DrawLine(threadPen, new Point(90 * s, 10 * s), new Point(90 * s, 246 * s));
            dc.DrawLine(threadPen, new Point(166 * s, 10 * s), new Point(166 * s, 246 * s));
        }

        // C. 水平スラット（5本）
        DrawSlats(dc, size);

        // D. 窓内側の立体リム陰影
        if (size >= 32)
        {
            var innerRimPen = new Pen(new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)), Math.Max(1.0, size * 0.008));
            innerRimPen.Freeze();
            dc.DrawEllipse(null, innerRimPen, new Point(center, center), innerR - 0.5, innerR - 0.5);
        }

        dc.Pop(); // Pop clip

        // 3. ベゼルのトップエッジハイライト（光の反射）
        if (size >= 32)
        {
            var arcPen = new Pen(new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)), Math.Max(1.2, size * (2.5 / 256.0)))
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            arcPen.Freeze();

            var arcPath = new StreamGeometry();
            using (var ctx = arcPath.Open())
            {
                double s = size / 256.0;
                ctx.BeginFigure(new Point(50 * s, 70 * s), false, false);
                ctx.ArcTo(new Point(180 * s, 28 * s), new Size(outerR, outerR), 0, false, SweepDirection.Clockwise, true, false);
            }
            arcPath.Freeze();
            dc.DrawGeometry(null, arcPen, arcPath);
        }
    }

    private static void DrawSlats(DrawingContext dc, int size)
    {
        var slateBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x4B, 0x62));
        var slateDarkBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x42, 0x57));
        var yellowBrush = new SolidColorBrush(Color.FromRgb(0xFD, 0xD8, 0x35));
        var yellowCoreBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xF5, 0x9D));
        var greenBrush = new SolidColorBrush(Color.FromRgb(0x43, 0xA0, 0x47));
        var greenCoreBrush = new SolidColorBrush(Color.FromRgb(0xA5, 0xD6, 0xA7));

        slateBrush.Freeze();
        slateDarkBrush.Freeze();
        yellowBrush.Freeze();
        yellowCoreBrush.Freeze();
        greenBrush.Freeze();
        greenCoreBrush.Freeze();

        if (size == 16)
        {
            // 16px ピクセルグリッドスナップ
            dc.DrawRectangle(slateBrush, null, new Rect(4, 3, 8, 1));
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0xFF, 0xEB, 0x3B)), null, new Rect(3, 5, 10, 2), 1, 1);
            dc.DrawRectangle(slateDarkBrush, null, new Rect(5, 8, 6, 1));
            dc.DrawRoundedRectangle(greenBrush, null, new Rect(3, 10, 10, 2), 1, 1);
            dc.DrawRectangle(slateBrush, null, new Rect(4, 13, 8, 1));
        }
        else if (size == 24)
        {
            // 24px
            dc.DrawRoundedRectangle(slateBrush, null, new Rect(6, 5, 12, 1.5), 0.75, 0.75);
            dc.DrawRoundedRectangle(yellowBrush, null, new Rect(5, 8, 14, 2.5), 1.25, 1.25);
            dc.DrawRoundedRectangle(slateDarkBrush, null, new Rect(7, 12, 10, 1.5), 0.75, 0.75);
            dc.DrawRoundedRectangle(greenBrush, null, new Rect(5, 15, 14, 2.5), 1.25, 1.25);
            dc.DrawRoundedRectangle(slateBrush, null, new Rect(6, 19, 12, 1.5), 0.75, 0.75);
        }
        else if (size == 32)
        {
            // 32px ピクセルグリッドスナップ（タスクバー特化）
            dc.DrawRoundedRectangle(slateBrush, null, new Rect(8, 7, 16, 2), 1, 1);

            dc.DrawRoundedRectangle(yellowBrush, null, new Rect(6, 11, 20, 3), 1.5, 1.5);
            dc.DrawRoundedRectangle(yellowCoreBrush, null, new Rect(10, 12, 12, 1), 0.5, 0.5);

            dc.DrawRoundedRectangle(slateDarkBrush, null, new Rect(9, 16, 14, 2), 1, 1);

            dc.DrawRoundedRectangle(greenBrush, null, new Rect(6, 20, 20, 3), 1.5, 1.5);
            dc.DrawRoundedRectangle(greenCoreBrush, null, new Rect(10, 21, 12, 1), 0.5, 0.5);

            dc.DrawRoundedRectangle(slateBrush, null, new Rect(7, 25, 18, 2), 1, 1);
        }
        else
        {
            // 48px, 64px, 128px, 256px
            double s = size / 256.0;

            // Slat 1
            double r1 = 7.5 * s;
            dc.DrawRoundedRectangle(slateBrush, null, new Rect(52 * s, 58 * s, 152 * s, 15 * s), r1, r1);

            // Slat 2 (Yellow)
            double r2 = 8.5 * s;
            dc.DrawRoundedRectangle(yellowBrush, null, new Rect(36 * s, 90 * s, 184 * s, 17 * s), r2, r2);
            if (size >= 64)
            {
                dc.DrawRoundedRectangle(yellowCoreBrush, null, new Rect(74 * s, 92 * s, 108 * s, 13 * s), 6.5 * s, 6.5 * s);
            }

            // Slat 3
            double r3 = 7.5 * s;
            dc.DrawRoundedRectangle(slateDarkBrush, null, new Rect(64 * s, 123 * s, 128 * s, 15 * s), r3, r3);

            // Slat 4 (Green)
            double r4 = 8.5 * s;
            dc.DrawRoundedRectangle(greenBrush, null, new Rect(36 * s, 154 * s, 184 * s, 17 * s), r4, r4);
            if (size >= 64)
            {
                dc.DrawRoundedRectangle(greenCoreBrush, null, new Rect(74 * s, 156 * s, 108 * s, 13 * s), 6.5 * s, 6.5 * s);
            }

            // Slat 5
            double r5 = 7.5 * s;
            dc.DrawRoundedRectangle(slateBrush, null, new Rect(48 * s, 187 * s, 160 * s, 15 * s), r5, r5);
        }
    }

    #endregion

    #region エンコード

    private static byte[] EncodePng(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>
    /// ICO 内の BMP フレームを作る。BITMAPINFOHEADER の高さは
    /// 「色 + マスク」の 2 枚ぶんを指定し、画素は下から上へ並べる決まり。
    /// 透過は 32bit の α で表すので、AND マスクは全 0（＝不透明）でよい。
    /// </summary>
    private static byte[] EncodeBmp(BitmapSource bitmap)
    {
        int width = bitmap.PixelWidth;
        int height = bitmap.PixelHeight;
        int stride = width * 4;

        var pixels = new byte[stride * height];
        bitmap.CopyPixels(pixels, stride, 0);

        int maskStride = (width + 31) / 32 * 4;
        int maskSize = maskStride * height;

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write(40);                       // biSize
        writer.Write(width);                    // biWidth
        writer.Write(height * 2);               // biHeight（色 + マスク）
        writer.Write((short)1);                 // biPlanes
        writer.Write((short)32);                // biBitCount
        writer.Write(0);                        // biCompression = BI_RGB
        writer.Write(stride * height + maskSize);
        writer.Write(0);                        // biXPelsPerMeter
        writer.Write(0);                        // biYPelsPerMeter
        writer.Write(0);                        // biClrUsed
        writer.Write(0);                        // biClrImportant

        for (int y = height - 1; y >= 0; y--) writer.Write(pixels, y * stride, stride);
        writer.Write(new byte[maskSize]);

        writer.Flush();
        return stream.ToArray();
    }

    private static byte[] BuildIco(List<(int Size, byte[] Data, bool Png)> frames)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write((short)0);                 // 予約
        writer.Write((short)1);                 // 1 = アイコン
        writer.Write((short)frames.Count);

        int offset = 6 + 16 * frames.Count;
        foreach (var (size, data, _) in frames)
        {
            writer.Write((byte)(size >= 256 ? 0 : size));   // 256 は 0 で表す
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)0);              // パレット数
            writer.Write((byte)0);              // 予約
            writer.Write((short)1);             // プレーン数
            writer.Write((short)32);            // ビット深度
            writer.Write(data.Length);
            writer.Write(offset);
            offset += data.Length;
        }

        foreach (var (_, data, _) in frames) writer.Write(data);

        writer.Flush();
        return stream.ToArray();
    }

    #endregion
}
