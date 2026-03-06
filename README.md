# PDF Editor

Editeur PDF de bureau pour Windows, construit avec WinUI 3 et iText 9.

## Fonctionnalites

- **Ouvrir / Creer / Sauvegarder** des documents PDF
- **Navigation** entre les pages avec pre-rendu des pages adjacentes
- **Zoom** avant/arriere
- **Edition de texte** : selection, modification, deplacement de blocs de texte
- **Insertion de contenu** : texte, images, formes (lignes, rectangles, cercles)
- **Champs de formulaire** : creation et edition (texte, case a cocher, bouton radio, liste deroulante, image, date)
- **Gestion des pages** : modal avec miniatures, drag-and-drop pour reordonner, ajout de pages blanches, suppression, insertion depuis un autre PDF
- **Barre de formatage** : police, taille, gras, italique, souligne, couleur
- **Grille et snap** : alignement magnetique sur la grille et les champs existants
- **Undo/Redo** complet (Ctrl+Z / Ctrl+Y)

## Prerequis

- Windows 10 (1809+) ou Windows 11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

## Build

```powershell
dotnet build src/PdfEditor.App/PdfEditor.App.csproj -c Release
```

## Publier un executable

```powershell
# Executable autonome (pas besoin de .NET installe)
dotnet publish src/PdfEditor.App/PdfEditor.App.csproj -c Release -r win-x64 --self-contained

# Executable unique
dotnet publish src/PdfEditor.App/PdfEditor.App.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

L'executable sera dans `src/PdfEditor.App/bin/Release/net8.0-windows10.0.22621.0/win-x64/publish/`.

## Tests

```powershell
dotnet test
```

## Architecture

```
src/
  PdfEditor.Core/          # Logique metier, services PDF
    Services/
      IPdfDocumentService   # Ouvrir, sauvegarder, gestion des pages
      IPdfRenderService     # Rendu des pages en bitmap
      IPdfTextService       # Extraction et modification de texte
      IPdfFormService       # Champs de formulaire
      IPdfContentService    # Insertion de contenu
      UndoRedoService       # Snapshots pour undo/redo
    Models/                 # PdfDocumentModel, TextBlock, FormField...
  PdfEditor.App/            # Interface WinUI 3
    Views/                  # PdfViewerPage (XAML + code-behind)
    ViewModels/             # MainViewModel (MVVM)
    Helpers/                # BitmapHelper (SkiaSharp -> WriteableBitmap)
tests/
  PdfEditor.Core.Tests/     # Tests unitaires
```

## Technologies

| Librairie | Utilisation |
|-----------|-------------|
| [WinUI 3](https://learn.microsoft.com/windows/apps/winui/winui3/) | Interface utilisateur |
| [iText 9](https://itextpdf.com/) | Manipulation PDF (AGPL-3.0) |
| [PDFtoImage](https://github.com/sungaila/PDFtoImage) | Rendu PDF en bitmap |
| [SkiaSharp](https://github.com/mono/SkiaSharp) | Traitement d'images |
| [CommunityToolkit.Mvvm](https://learn.microsoft.com/dotnet/communitytoolkit/mvvm/) | Pattern MVVM |

## Licence

Ce projet est sous licence [AGPL-3.0](LICENSE) (imposee par la dependance iText).
