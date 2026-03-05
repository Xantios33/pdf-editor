using PdfEditor.Core.Models;

namespace PdfEditor.Core.Services;

public interface IPdfDocumentService
{
    PdfDocumentModel Open(string filePath);
    void Save(PdfDocumentModel document);
    void SaveAs(PdfDocumentModel document, string newPath);
    void Close(PdfDocumentModel document);
}
