using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Force.Halo.Checkpoints.Linux.Views
{
    public partial class UpdateWindow : Window
    {
        public string? Result { get; private set; }

        public UpdateWindow()
        {
            InitializeComponent();
        }

        private void DownloadOnlyButton_Click(object? sender, RoutedEventArgs e)
        {
            Result = "download";
            Close();
        }

        private void DownloadReplaceButton_Click(object? sender, RoutedEventArgs e)
        {
            Result = "replace";
            Close();
        }

        private void CloseButton_Click(object? sender, RoutedEventArgs e)
        {
            Result = null;
            Close();
        }
    }
}
