using PDFtoImage;
using SkiaSharp;

namespace PdfEditor.Core.Services;

public class PdfRenderService : IPdfRenderService
{
    public SKBitmap RenderPage(string filePath, int pageIndex, int dpi = 300)
    {
        using var stream = File.OpenRead(filePath);
        var options = new RenderOptions(
            Dpi: dpi,
            WithAnnotations: true,
            WithFormFill: true
        );
        return Conversion.ToImage(stream, pageIndex, leaveOpen: false, password: null, options: options);
    }

    public async Task<SKBitmap> RenderPageAsync(string filePath, int pageIndex, int dpi = 300)
    {
        return await Task.Run(() => RenderPage(filePath, pageIndex, dpi));
    }
}
