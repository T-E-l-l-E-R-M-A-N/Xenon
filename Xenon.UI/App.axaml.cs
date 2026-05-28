using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Microsoft.Maui.Storage;
using Xenon.Core;
using Xenon.UI.Views;

namespace Xenon.UI;

public partial class App : Application
{
    public static readonly string AppDataPath =
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX) || RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ||
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            ? Environment.CurrentDirectory + "/media"
            : FileSystem.AppDataDirectory + "/media";
    
    public MainViewModel ApplicationViewModel { get; set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        if (!Directory.Exists(AppDataPath))
            Directory.CreateDirectory(AppDataPath);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        ApplicationViewModel = IoC.Resolve<MainViewModel>();
        ApplicationViewModel.Init();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Avoid duplicate validations from both Avalonia and the CommunityToolkit. 
            // More info: https://docs.avaloniaui.net/docs/guides/development-guides/data-validation#manage-validationplugins
            DisableAvaloniaDataAnnotationValidation();
            var window = new Window()
            {
                DataContext = ApplicationViewModel,
                Content = new MainView(),
                Width = 1024,
                Height = 768
            };

            desktop.MainWindow = window;
            desktop.MainWindow.AttachDevTools();

        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
            singleView.MainView = new MainView()
            {
                DataContext = ApplicationViewModel
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
    private void DisableAvaloniaDataAnnotationValidation()
    {
        // Get an array of plugins to remove
        var dataValidationPluginsToRemove =
            BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();

        // remove each entry found
        foreach (var plugin in dataValidationPluginsToRemove)
        {
            BindingPlugins.DataValidators.Remove(plugin);
        }
    }
}