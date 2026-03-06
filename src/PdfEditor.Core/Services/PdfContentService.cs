using iText.IO.Image;
using iText.Kernel.Colors;
using iText.Kernel.Font;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using PdfEditor.Core.Models;
using StandardFonts = iText.IO.Font.Constants.StandardFonts;

namespace PdfEditor.Core.Services;

public class PdfContentService : IPdfContentService
{
    public void InsertText(string filePath, InsertTextParams p)
    {
        var fileBytes = File.ReadAllBytes(filePath);
        using var inputStream = new MemoryStream(fileBytes);
        using var reader = new PdfReader(inputStream);
        using var writer = new PdfWriter(filePath);
        using var pdfDoc = new PdfDocument(reader, writer);

        var page = pdfDoc.GetPage(p.PageIndex + 1);
        var canvas = new PdfCanvas(page);
        canvas.SaveState();

        var fillColor = new DeviceRgb(p.ColorR, p.ColorG, p.ColorB);
        canvas.SetFillColor(fillColor);

        var font = ResolveFont(p.FontName, p.IsBold, p.IsItalic);

        canvas.BeginText();
        canvas.SetFontAndSize(font, p.FontSize);
        canvas.MoveText(p.X, p.Y);
        canvas.ShowText(p.Text);
        canvas.EndText();

        canvas.RestoreState();
        pdfDoc.Close();
    }

    public void InsertImage(string filePath, InsertImageParams p)
    {
        var fileBytes = File.ReadAllBytes(filePath);
        using var inputStream = new MemoryStream(fileBytes);
        using var reader = new PdfReader(inputStream);
        using var writer = new PdfWriter(filePath);
        using var pdfDoc = new PdfDocument(reader, writer);

        var page = pdfDoc.GetPage(p.PageIndex + 1);
        var canvas = new PdfCanvas(page);

        var imageData = ImageDataFactory.Create(p.ImageFilePath);
        var rect = new Rectangle(p.X, p.Y, p.Width, p.Height);
        canvas.AddImageFittedIntoRectangle(imageData, rect, false);

        pdfDoc.Close();
    }

    public void InsertShape(string filePath, InsertShapeParams p)
    {
        var fileBytes = File.ReadAllBytes(filePath);
        using var inputStream = new MemoryStream(fileBytes);
        using var reader = new PdfReader(inputStream);
        using var writer = new PdfWriter(filePath);
        using var pdfDoc = new PdfDocument(reader, writer);

        var page = pdfDoc.GetPage(p.PageIndex + 1);
        var canvas = new PdfCanvas(page);
        canvas.SaveState();

        var strokeColor = new DeviceRgb(p.StrokeR, p.StrokeG, p.StrokeB);
        canvas.SetStrokeColor(strokeColor);
        canvas.SetLineWidth(p.StrokeWidth);

        var hasFill = p.FillR.HasValue && p.FillG.HasValue && p.FillB.HasValue;
        if (hasFill)
        {
            var fillColor = new DeviceRgb(p.FillR!.Value, p.FillG!.Value, p.FillB!.Value);
            canvas.SetFillColor(fillColor);
        }

        switch (p.ShapeType)
        {
            case InsertionTool.Line:
                canvas.MoveTo(p.X, p.Y);
                canvas.LineTo(p.X + p.Width, p.Y + p.Height);
                canvas.Stroke();
                break;

            case InsertionTool.Rectangle:
                canvas.Rectangle(p.X, p.Y, p.Width, p.Height);
                if (hasFill)
                    canvas.FillStroke();
                else
                    canvas.Stroke();
                break;

            case InsertionTool.Circle:
                canvas.Ellipse(p.X, p.Y, p.X + p.Width, p.Y + p.Height);
                if (hasFill)
                    canvas.FillStroke();
                else
                    canvas.Stroke();
                break;
        }

        canvas.RestoreState();
        pdfDoc.Close();
    }

    private static PdfFont ResolveFont(string? fontName, bool isBold, bool isItalic)
    {
        var family = DetectFontFamily(fontName);

        var standardFontName = family switch
        {
            FontFamily.Times => (isBold, isItalic) switch
            {
                (true, true) => StandardFonts.TIMES_BOLDITALIC,
                (true, false) => StandardFonts.TIMES_BOLD,
                (false, true) => StandardFonts.TIMES_ITALIC,
                _ => StandardFonts.TIMES_ROMAN
            },
            FontFamily.Courier => (isBold, isItalic) switch
            {
                (true, true) => StandardFonts.COURIER_BOLDOBLIQUE,
                (true, false) => StandardFonts.COURIER_BOLD,
                (false, true) => StandardFonts.COURIER_OBLIQUE,
                _ => StandardFonts.COURIER
            },
            _ => (isBold, isItalic) switch
            {
                (true, true) => StandardFonts.HELVETICA_BOLDOBLIQUE,
                (true, false) => StandardFonts.HELVETICA_BOLD,
                (false, true) => StandardFonts.HELVETICA_OBLIQUE,
                _ => StandardFonts.HELVETICA
            }
        };

        try
        {
            return PdfFontFactory.CreateFont(standardFontName);
        }
        catch
        {
            return PdfFontFactory.CreateFont(StandardFonts.HELVETICA);
        }
    }

    private static FontFamily DetectFontFamily(string? fontName)
    {
        if (string.IsNullOrEmpty(fontName))
            return FontFamily.Helvetica;

        var upper = fontName.ToUpperInvariant();

        if (upper.Contains("TIMES") || upper.Contains("SERIF") || upper.Contains("ROMAN"))
            return FontFamily.Times;

        if (upper.Contains("COURIER") || upper.Contains("MONO") || upper.Contains("CONSOL"))
            return FontFamily.Courier;

        return FontFamily.Helvetica;
    }

    private enum FontFamily
    {
        Helvetica,
        Times,
        Courier
    }
}
