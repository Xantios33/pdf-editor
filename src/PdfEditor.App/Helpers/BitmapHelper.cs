using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media.Imaging;
using SkiaSharp;

namespace PdfEditor.App.Helpers;

public static class BitmapHelper
{
    public static WriteableBitmap ToWriteableBitmap(SKBitmap skBitmap)
    {
        var width = skBitmap.Width;
        var height = skBitmap.Height;
        var bitmap = new WriteableBitmap(width, height);

        // Ensure BGRA8888 format for WriteableBitmap compatibility
        // Do NOT dispose the original bitmap — it may be cached.
        var needsConversion = skBitmap.ColorType != SKColorType.Bgra8888;
        var converted = needsConversion ? ConvertToBgra8888(skBitmap) : skBitmap;

        try
        {
            var pixels = converted.GetPixelSpan();
            using var stream = bitmap.PixelBuffer.AsStream();
            stream.Write(pixels);
        }
        finally
        {
            // Only dispose the temporary conversion, never the original
            if (needsConversion)
                converted.Dispose();
        }

        bitmap.Invalidate();
        return bitmap;
    }

    private static SKBitmap ConvertToBgra8888(SKBitmap source)
    {
        var info = new SKImageInfo(source.Width, source.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var converted = new SKBitmap(info);
        source.CopyTo(converted, SKColorType.Bgra8888);
        return converted;
    }
}
