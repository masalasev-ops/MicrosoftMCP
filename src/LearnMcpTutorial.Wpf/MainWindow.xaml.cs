using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LearnMcpTutorial.Wpf.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace LearnMcpTutorial.Wpf;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow()
    {
        InitializeComponent();
        _vm = App.Services.GetRequiredService<MainViewModel>();
        DataContext = _vm;

        ApiKeyBox.PasswordChanged += (_, _) =>
        {
            _vm.ApiKey = ApiKeyBox.Password;
        };
    }

    private void SourceUrl_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is TextBlock tb && !string.IsNullOrWhiteSpace(tb.Text))
        {
            var text = tb.Text;
            var dotIndex = text.IndexOf(". ");
            var url = dotIndex >= 0 ? text[(dotIndex + 2)..] : text;

            if (Uri.TryCreate(url, UriKind.Absolute, out var uri)
                && (uri.Scheme == "http" || uri.Scheme == "https"))
            {
                Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
            }
        }
    }

    protected override async void OnClosed(EventArgs e)
    {
        await _vm.DisposeAsync();
        base.OnClosed(e);
    }
}
