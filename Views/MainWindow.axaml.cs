using System;
using Avalonia.Controls;

namespace AriaUI.Views;

public partial class MainWindow : Window
{
    public bool IsExplicitExit { get; set; }

    public MainWindow()
    {
        InitializeComponent();

        Closing += (s, e) =>
        {
            if (!IsExplicitExit)
            {
                e.Cancel = true;
                Hide();
            }
        };
    }
}