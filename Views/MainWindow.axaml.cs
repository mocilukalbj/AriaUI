using System;
using System.Threading.Tasks;
using Avalonia.Controls;

namespace AriaUI.Views;

public partial class MainWindow : Window
{
    private bool _isShutdownCompleted;
    private bool _isShuttingDown;
    private Func<Task>? _asyncShutdownHandler;

    public MainWindow()
    {
        InitializeComponent();

        Closing += async (s, e) =>
        {
            if (_isShutdownCompleted)
            {
                return;
            }

            e.Cancel = true;

            if (_isShuttingDown)
            {
                return;
            }
            _isShuttingDown = true;

            Hide();

            try
            {
                if (_asyncShutdownHandler != null)
                {
                    await _asyncShutdownHandler();
                }
            }
            finally
            {
                _isShutdownCompleted = true;
                Close();
            }
        };
    }

    public void RegisterAsyncShutdownHandler(Func<Task> handler)
    {
        _asyncShutdownHandler = handler;
    }
}
