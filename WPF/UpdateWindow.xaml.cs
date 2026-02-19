using System.Windows;
using System.Windows.Input;

namespace Force.Halo.Checkpoints
{
    public partial class UpdateWindow : Window
    {
        public string? Result { get; private set; }

        public UpdateWindow()
        {
            InitializeComponent();
        }

        private void Border_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
                DragMove();
        }

        private void DownloadOnlyButton_Click(object sender, RoutedEventArgs e)
        {
            Result = "download";
            Close();
        }

        private void DownloadReplaceButton_Click(object sender, RoutedEventArgs e)
        {
            Result = "replace";
            Close();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Result = null;
            Close();
        }

        private void CloseWithEnterKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                Result = null;
                Close();
            }
        }
    }
}
