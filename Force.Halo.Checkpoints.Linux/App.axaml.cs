using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Avalonia.Markup.Xaml;
using Force.Halo.Checkpoints.Linux.ViewModels;
using Force.Halo.Checkpoints.Linux.Views;

namespace Force.Halo.Checkpoints.Linux;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Avoid duplicate validations from both Avalonia and the CommunityToolkit. 
            // More info: https://docs.avaloniaui.net/docs/guides/development-guides/data-validation#manage-validationplugins
            DisableAvaloniaDataAnnotationValidation();
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    // Stock Avalonia template code. It only reads the plugin list to take the data
    // annotation validator back out of it, so the reflection the trimmer is worried about
    // is in a code path this program removes rather than uses.
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "Only enumerates the validator list in order to remove an entry from it.")]
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