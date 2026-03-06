using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using PdfEditor.Core.Services;

namespace PdfEditor.App;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    public static Window MainWindow { get; private set; } = null!;

    public App()
    {
        this.InitializeComponent();

        var services = new ServiceCollection();

        // Core services
        services.AddSingleton<IPdfRenderService, PdfRenderService>();
        services.AddSingleton<IPdfDocumentService, PdfDocumentService>();
        services.AddSingleton<IPdfTextService, PdfTextService>();
        services.AddSingleton<IPdfFormService, PdfFormService>();
        services.AddSingleton<IPdfContentService, PdfContentService>();
        services.AddSingleton<UndoRedoService>();

        // ViewModels
        services.AddTransient<ViewModels.MainViewModel>();

        Services = services.BuildServiceProvider();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWindow = new MainWindow();
        MainWindow.Activate();
    }
}
