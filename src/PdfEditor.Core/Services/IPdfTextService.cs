using PdfEditor.Core.Models;

namespace PdfEditor.Core.Services;

public interface IPdfTextService
{
    List<TextBlock> ExtractTextBlocks(string filePath, int pageIndex);
    void ReplaceText(string filePath, TextBlock originalBlock, string newText);
    void MoveTextBlock(string filePath, TextBlock block, float newX, float newY);
}
