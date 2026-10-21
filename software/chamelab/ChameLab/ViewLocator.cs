using Avalonia.Controls;
using Avalonia.Controls.Templates;
using ChameLab.ViewModels;
using ChameLab.Views;

namespace ChameLab;

/// <summary>
/// Given a view model, returns the corresponding view if possible.
/// Uses an explicit map instead of reflection so the views survive trimming
/// (the browser/WASM build is trimmed, and <c>Type.GetType</c> then returns null).
/// </summary>
public class ViewLocator : IDataTemplate
{
    public Control? Build(object? param) => param switch
    {
        null => null,
        MainViewModel => new MainView(),
        ConnectViewModel => new ConnectView(),
        DashboardViewModel => new DashboardView(),
        SlotsViewModel => new SlotsView(),
        CardsViewModel => new CardsView(),
        DictionariesViewModel => new DictionariesView(),
        ToolsViewModel => new ToolsView(),
        SettingsViewModel => new SettingsView(),
        _ => new TextBlock { Text = "Not Found: " + param.GetType().FullName!.Replace("ViewModel", "View") },
    };

    public bool Match(object? data)
    {
        return data is ViewModelBase;
    }
}
