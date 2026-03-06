namespace PdfEditor.Core.Models;

public class PdfDocumentModel
{
    public required string FilePath { get; set; }
    public string? WorkingCopyPath { get; set; }
    public int PageCount { get; set; }
    public bool HasUnsavedChanges { get; set; }
    public bool IsNew { get; set; }

    /// <summary>
    /// Returns the path to use for rendering/reading (working copy if available).
    /// </summary>
    public string ActivePath => WorkingCopyPath ?? FilePath;
}
