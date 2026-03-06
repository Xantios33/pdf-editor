using PdfEditor.Core.Models;

namespace PdfEditor.Core.Services;

public interface IPdfContentService
{
    void InsertText(string filePath, InsertTextParams parameters);
    void InsertImage(string filePath, InsertImageParams parameters);
    void InsertShape(string filePath, InsertShapeParams parameters);
}
