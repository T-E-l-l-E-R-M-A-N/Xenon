using Avalonia.Controls.Templates;

namespace Xenon.UI.Helpers;

public class ViewLocator : IDataTemplate
{
    public Control? Build(object? param)
    {
        if (param is null)
            return null;

        var name = param.GetType().Name!.Replace("ViewModel", "");
        var type = this.GetType().Assembly.DefinedTypes.FirstOrDefault(x=>x.Name == name);

        if (type != null)
        {
            try
            {
                return (Control)Activator.CreateInstance(type)!;
            }
            catch (Exception e)
            {
                return new TextBlock { Text = "Not Found: " + name };
            }
        }

        return new TextBlock { Text = "Not Found: " + name };
    }

    public bool Match(object? data)
    {
        return data is ViewModelBase;
    }
}