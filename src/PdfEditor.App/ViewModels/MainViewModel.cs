using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfEditor.Core.Models;
using PdfEditor.Core.Services;
using SkiaSharp;

namespace PdfEditor.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IPdfRenderService _renderService;
    private readonly IPdfDocumentService _documentService;
    private readonly IPdfTextService _textService;
    private readonly IPdfFormService _formService;
    private readonly UndoRedoService _undoRedo;

    private PdfDocumentModel? _document;
    private readonly Dictionary<int, SKBitmap> _pageCache = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageIndicator))]
    [NotifyPropertyChangedFor(nameof(HasDocument))]
    private int _currentPageIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageIndicator))]
    [NotifyPropertyChangedFor(nameof(HasDocument))]
    private int _pageCount;

    [ObservableProperty]
    private SKBitmap? _currentPageBitmap;

    [ObservableProperty]
    private string _statusText = "Prêt";

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _canUndo;

    [ObservableProperty]
    private bool _canRedo;

    [ObservableProperty]
    private List<TextBlock>? _currentTextBlocks;

    [ObservableProperty]
    private List<FormField>? _currentFormFields;

    public bool HasDocument => _document != null;
    public string PageIndicator => HasDocument ? $"{CurrentPageIndex + 1} / {PageCount}" : "";
    public PdfDocumentModel? Document => _document;

    public MainViewModel(
        IPdfRenderService renderService,
        IPdfDocumentService documentService,
        IPdfTextService textService,
        IPdfFormService formService,
        UndoRedoService undoRedo)
    {
        _renderService = renderService;
        _documentService = documentService;
        _textService = textService;
        _formService = formService;
        _undoRedo = undoRedo;
    }

    public async Task OpenDocumentAsync(string filePath)
    {
        try
        {
            IsLoading = true;
            StatusText = "Ouverture...";

            if (_document != null)
            {
                _documentService.Close(_document);
                ClearCache();
            }

            _undoRedo.Clear();
            UpdateUndoRedoState();

            _document = await Task.Run(() => _documentService.Open(filePath));
            PageCount = _document.PageCount;
            CurrentPageIndex = 0;

            await RenderCurrentPageAsync();
            StatusText = Path.GetFileName(filePath);
        }
        catch (Exception ex)
        {
            StatusText = $"Erreur: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanGoNextPage))]
    private async Task NextPage()
    {
        CurrentPageIndex++;
        await RenderCurrentPageAsync();
    }

    private bool CanGoNextPage() => _document != null && CurrentPageIndex < PageCount - 1;

    [RelayCommand(CanExecute = nameof(CanGoPrevPage))]
    private async Task PrevPage()
    {
        CurrentPageIndex--;
        await RenderCurrentPageAsync();
    }

    private bool CanGoPrevPage() => _document != null && CurrentPageIndex > 0;

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private async Task Save()
    {
        if (_document == null) return;
        try
        {
            StatusText = "Sauvegarde...";
            await Task.Run(() => _documentService.Save(_document));
            StatusText = $"Sauvegardé: {Path.GetFileName(_document.FilePath)}";
        }
        catch (Exception ex)
        {
            StatusText = $"Erreur: {ex.Message}";
        }
    }

    /// <summary>
    /// Extracts text blocks for the current page.
    /// </summary>
    public async Task ExtractTextBlocksAsync()
    {
        if (_document == null) return;

        try
        {
            CurrentTextBlocks = await Task.Run(() =>
                _textService.ExtractTextBlocks(_document.ActivePath, CurrentPageIndex));
            StatusText = $"{CurrentTextBlocks.Count} blocs de texte détectés";
        }
        catch (Exception ex)
        {
            StatusText = $"Erreur extraction: {ex.Message}";
        }
    }

    /// <summary>
    /// Replaces text in a block and re-renders the page.
    /// </summary>
    public async Task ReplaceTextAsync(TextBlock block, string newText)
    {
        if (_document == null) return;

        try
        {
            IsLoading = true;
            StatusText = "Modification...";

            _undoRedo.SaveSnapshot(_document.ActivePath);

            await Task.Run(() => _textService.ReplaceText(_document.ActivePath, block, newText));
            _document.HasUnsavedChanges = true;
            UpdateUndoRedoState();

            // Invalidate cache and re-render
            InvalidateCache(CurrentPageIndex);
            await RenderCurrentPageAsync();

            StatusText = "Texte modifié";
        }
        catch (Exception ex)
        {
            StatusText = $"Erreur: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task MoveTextBlockAsync(TextBlock block, float newX, float newY)
    {
        if (_document == null) return;

        try
        {
            IsLoading = true;
            StatusText = "Déplacement...";

            _undoRedo.SaveSnapshot(_document.ActivePath);

            await Task.Run(() => _textService.MoveTextBlock(_document.ActivePath, block, newX, newY));
            _document.HasUnsavedChanges = true;
            UpdateUndoRedoState();

            InvalidateCache(CurrentPageIndex);
            await RenderCurrentPageAsync();

            StatusText = "Bloc déplacé";
        }
        catch (Exception ex)
        {
            StatusText = $"Erreur: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task UndoAsync()
    {
        if (_document == null || !_undoRedo.CanUndo) return;

        try
        {
            IsLoading = true;
            StatusText = "Annulation...";

            await Task.Run(() => _undoRedo.Undo(_document.ActivePath));
            UpdateUndoRedoState();

            ClearCache();
            await RenderCurrentPageAsync();

            StatusText = "Annulé";
        }
        catch (Exception ex)
        {
            StatusText = $"Erreur: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task RedoAsync()
    {
        if (_document == null || !_undoRedo.CanRedo) return;

        try
        {
            IsLoading = true;
            StatusText = "Rétablissement...";

            await Task.Run(() => _undoRedo.Redo(_document.ActivePath));
            UpdateUndoRedoState();

            ClearCache();
            await RenderCurrentPageAsync();

            StatusText = "Rétabli";
        }
        catch (Exception ex)
        {
            StatusText = $"Erreur: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void UpdateUndoRedoState()
    {
        CanUndo = _undoRedo.CanUndo;
        CanRedo = _undoRedo.CanRedo;
    }

    private async Task RenderCurrentPageAsync()
    {
        if (_document == null) return;

        try
        {
            IsLoading = true;

            if (_pageCache.TryGetValue(CurrentPageIndex, out var cached))
            {
                CurrentPageBitmap = cached;
            }
            else
            {
                var bitmap = await _renderService.RenderPageAsync(
                    _document.ActivePath, CurrentPageIndex);
                _pageCache[CurrentPageIndex] = bitmap;
                CurrentPageBitmap = bitmap;
            }

            _ = PreRenderAdjacentPagesAsync();

            NextPageCommand.NotifyCanExecuteChanged();
            PrevPageCommand.NotifyCanExecuteChanged();
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task PreRenderAdjacentPagesAsync()
    {
        if (_document == null) return;

        var pagesToCache = new[] { CurrentPageIndex - 1, CurrentPageIndex + 1 };
        foreach (var pageIdx in pagesToCache)
        {
            if (pageIdx >= 0 && pageIdx < PageCount && !_pageCache.ContainsKey(pageIdx))
            {
                try
                {
                    var bitmap = await _renderService.RenderPageAsync(
                        _document.ActivePath, pageIdx);
                    _pageCache[pageIdx] = bitmap;
                }
                catch { }
            }
        }
    }

    private void ClearCache()
    {
        foreach (var bitmap in _pageCache.Values)
            bitmap.Dispose();
        _pageCache.Clear();
    }

    public void InvalidateCache(int pageIndex)
    {
        if (_pageCache.TryGetValue(pageIndex, out var bitmap))
        {
            bitmap.Dispose();
            _pageCache.Remove(pageIndex);
        }
    }

    public async Task ExtractFormFieldsAsync()
    {
        if (_document == null) return;

        try
        {
            CurrentFormFields = await Task.Run(() =>
                _formService.ExtractFormFields(_document.ActivePath, CurrentPageIndex));
        }
        catch (Exception ex)
        {
            StatusText = $"Erreur champs formulaire: {ex.Message}";
        }
    }

    public async Task SetFormFieldValueAsync(string fieldName, string value)
    {
        if (_document == null) return;

        try
        {
            IsLoading = true;
            StatusText = "Modification champ...";

            _undoRedo.SaveSnapshot(_document.ActivePath);

            await Task.Run(() => _formService.SetFieldValue(_document.ActivePath, fieldName, value));
            _document.HasUnsavedChanges = true;
            UpdateUndoRedoState();

            InvalidateCache(CurrentPageIndex);
            await RenderCurrentPageAsync();

            StatusText = "Champ modifié";
        }
        catch (Exception ex)
        {
            StatusText = $"Erreur: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }
}
