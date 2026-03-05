using SkiaSharp;

namespace PdfEditor.Core.Services;

public interface IPdfRenderService
{
    SKBitmap RenderPage(string filePath, int pageIndex, int dpi = 300);
    Task<SKBitmap> RenderPageAsync(string filePath, int pageIndex, int dpi = 300);
}
