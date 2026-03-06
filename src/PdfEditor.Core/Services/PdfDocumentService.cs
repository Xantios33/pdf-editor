using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using PdfEditor.Core.Models;
using Path = System.IO.Path;

namespace PdfEditor.Core.Services;

public class PdfDocumentService : IPdfDocumentService
{
    public PdfDocumentModel CreateBlank(int pageCount = 1, float width = 595f, float height = 842f)
    {
        var tempPath = Path.Combine(
            Path.GetTempPath(),
            $"pdfedit_{Guid.NewGuid():N}.pdf");

        using var writer = new PdfWriter(tempPath);
        using var pdfDoc = new PdfDocument(writer);
        var pageSize = new PageSize(width, height);

        for (int i = 0; i < pageCount; i++)
            pdfDoc.AddNewPage(pageSize);

        pdfDoc.Close();

        return new PdfDocumentModel
        {
            FilePath = tempPath,
            WorkingCopyPath = tempPath,
            PageCount = pageCount,
            HasUnsavedChanges = true,
            IsNew = true,
        };
    }

    public PdfDocumentModel Open(string filePath)
    {
        using var reader = new PdfReader(filePath);
        using var pdfDoc = new PdfDocument(reader);

        var workingCopy = Path.Combine(
            Path.GetTempPath(),
            $"pdfedit_{Guid.NewGuid():N}{Path.GetExtension(filePath)}");
        File.Copy(filePath, workingCopy, overwrite: true);

        return new PdfDocumentModel
        {
            FilePath = filePath,
            WorkingCopyPath = workingCopy,
            PageCount = pdfDoc.GetNumberOfPages(),
            HasUnsavedChanges = false
        };
    }

    public void Save(PdfDocumentModel document)
    {
        if (document.WorkingCopyPath != null)
        {
            File.Copy(document.WorkingCopyPath, document.FilePath, overwrite: true);
            document.HasUnsavedChanges = false;
        }
    }

    public void SaveAs(PdfDocumentModel document, string newPath)
    {
        var sourcePath = document.WorkingCopyPath ?? document.FilePath;
        File.Copy(sourcePath, newPath, overwrite: true);
        document.FilePath = newPath;
        document.HasUnsavedChanges = false;
    }

    public void Close(PdfDocumentModel document)
    {
        if (document.WorkingCopyPath != null && File.Exists(document.WorkingCopyPath))
        {
            try { File.Delete(document.WorkingCopyPath); }
            catch { /* Best effort cleanup */ }
        }
    }
}
