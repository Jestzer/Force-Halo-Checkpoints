using System;
using System.Diagnostics.CodeAnalysis;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Force.Halo.Checkpoints.Linux.ViewModels;

namespace Force.Halo.Checkpoints.Linux;

public class ViewLocator : IDataTemplate
{
    // Match() only fires for a ViewModelBase, and this program never renders one as
    // content - MainWindow builds its controls directly, and MainWindowViewModel exists
    // but is never used as a DataContext. So this name-to-type lookup is unreachable in
    // practice and the trimmer's complaint about it can't bite. It's left in place
    // because it's the stock Avalonia template code and removing it would just be a
    // trap for the next person who does start using view models here - at which point
    // this suppression needs to go and the lookup needs replacing with something the
    // trimmer can see through.
    [UnconditionalSuppressMessage("Trimming", "IL2057",
        Justification = "Unreachable: nothing in this program renders a ViewModelBase as content.")]
    public Control? Build(object? param)
    {
        if (param is null)
            return null;
        
        var name = param.GetType().FullName!.Replace("ViewModel", "View", StringComparison.Ordinal);
        var type = Type.GetType(name);

        if (type != null)
        {
            return (Control)Activator.CreateInstance(type)!;
        }
        
        return new TextBlock { Text = "Not Found: " + name };
    }

    public bool Match(object? data)
    {
        return data is ViewModelBase;
    }
}
