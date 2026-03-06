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

    public int GetPageCount(string filePath)
    {
        using var reader = new PdfReader(filePath);
        using var pdfDoc = new PdfDocument(reader);
        return pdfDoc.GetNumberOfPages();
    }

    public void AddBlankPage(string filePath, int insertAtIndex)
    {
        var bytes = File.ReadAllBytes(filePath);
        using var readStream = new MemoryStream(bytes);
        using var reader = new PdfReader(readStream);
        using var writer = new PdfWriter(filePath);
        using var pdfDoc = new PdfDocument(reader, writer);

        var pageSize = insertAtIndex >= 0 && insertAtIndex < pdfDoc.GetNumberOfPages()
            ? pdfDoc.GetPage(insertAtIndex + 1).GetPageSize()
            : PageSize.A4;

        pdfDoc.AddNewPage(insertAtIndex + 2, new PageSize(pageSize));
        pdfDoc.Close();
    }

    public int InsertPagesFrom(string filePath, string sourcePdfPath, int insertAtIndex)
    {
        var bytes = File.ReadAllBytes(filePath);
        using var readStream = new MemoryStream(bytes);
        using var reader = new PdfReader(readStream);
        using var writer = new PdfWriter(filePath);
        using var targetDoc = new PdfDocument(reader, writer);

        using var sourceReader = new PdfReader(sourcePdfPath);
        using var sourceDoc = new PdfDocument(sourceReader);

        int sourcePageCount = sourceDoc.GetNumberOfPages();
        sourceDoc.CopyPagesTo(1, sourcePageCount, targetDoc, insertAtIndex + 2);

        targetDoc.Close();
        sourceDoc.Close();

        return sourcePageCount;
    }

    public void DeletePage(string filePath, int pageIndex)
    {
        var bytes = File.ReadAllBytes(filePath);
        using var readStream = new MemoryStream(bytes);
        using var reader = new PdfReader(readStream);
        using var writer = new PdfWriter(filePath);
        using var pdfDoc = new PdfDocument(reader, writer);

        pdfDoc.RemovePage(pageIndex + 1);
        pdfDoc.Close();
    }

    public void ReorderPages(string filePath, int[] newOrder)
    {
        var bytes = File.ReadAllBytes(filePath);
        var tempPath = Path.Combine(Path.GetTempPath(), $"pdfedit_reorder_{Guid.NewGuid():N}.pdf");

        using (var sourceStream = new MemoryStream(bytes))
        using (var sourceReader = new PdfReader(sourceStream))
        using (var sourceDoc = new PdfDocument(sourceReader))
        using (var targetWriter = new PdfWriter(tempPath))
        using (var targetDoc = new PdfDocument(targetWriter))
        {
            foreach (int oldIndex in newOrder)
            {
                sourceDoc.CopyPagesTo(oldIndex + 1, oldIndex + 1, targetDoc);
            }
            targetDoc.Close();
            sourceDoc.Close();
        }

        File.Copy(tempPath, filePath, overwrite: true);
        try { File.Delete(tempPath); } catch { }
    }
}
