using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using AriaUI.ViewModels;
using AriaUI.Views;

namespace AriaUI;

/// <summary>
/// AOT-safe strongly-typed ViewLocator mapping ViewModel types to View factories.
/// </summary>
public class ViewLocator : IDataTemplate
{
    private static readonly Dictionary<Type, Func<Control>> ViewMap = new()
    {
        [typeof(MainWindowViewModel)] = () => new MainWindow(),
        [typeof(TaskListViewModel)] = () => new TaskListView(),
        [typeof(SettingsViewModel)] = () => new SettingsView(),
    };

    public Control? Build(object? param)
    {
        if (param is null)
            return null;

        var type = param.GetType();
        if (ViewMap.TryGetValue(type, out var factory))
        {
            return factory();
        }

        return new TextBlock { Text = $"View not found for {type.Name}" };
    }

    public bool Match(object? data)
    {
        return data is ViewModelBase;
    }
}
