using PdfEditor.Core.Models;

namespace PdfEditor.Core.Services;

public interface IPdfDocumentService
{
    PdfDocumentModel Open(string filePath);
    PdfDocumentModel CreateBlank(int pageCount = 1, float width = 595f, float height = 842f);
    void Save(PdfDocumentModel document);
    void SaveAs(PdfDocumentModel document, string newPath);
    void Close(PdfDocumentModel document);
}
