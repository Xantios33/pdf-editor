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
        using var converted = skBitmap.ColorType == SKColorType.Bgra8888
            ? skBitmap
            : ConvertToBgra8888(skBitmap);

        var pixels = converted.GetPixelSpan();
        using var stream = bitmap.PixelBuffer.AsStream();
        stream.Write(pixels);

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
