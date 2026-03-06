using PdfEditor.Core.Models;

namespace PdfEditor.Core.Services;

public interface IPdfDocumentService
{
    PdfDocumentModel Open(string filePath);
    PdfDocumentModel CreateBlank(int pageCount = 1, float width = 595f, float height = 842f);
    void Save(PdfDocumentModel document);
    void SaveAs(PdfDocumentModel document, string newPath);
    void Close(PdfDocumentModel document);
    int GetPageCount(string filePath);
    void AddBlankPage(string filePath, int insertAtIndex);
    int InsertPagesFrom(string filePath, string sourcePdfPath, int insertAtIndex);
    void DeletePage(string filePath, int pageIndex);
    void ReorderPages(string filePath, int[] newOrder);
}
