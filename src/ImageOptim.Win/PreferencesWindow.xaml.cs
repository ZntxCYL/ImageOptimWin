using System.Windows;
using ImageOptim.Win.Services;
using Microsoft.Win32;

namespace ImageOptim.Win;

public partial class PreferencesWindow : Window
{
    public AppSettings Settings { get; }

    public PreferencesWindow(AppSettings settings)
    {
        Settings = settings;
        InitializeComponent();
        DataContext = Settings;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ThemeManager.ApplyWindowChrome(this);
    }

    private void BrowseTools_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择压缩器所在文件夹",
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true) ToolsPathBox.Text = dialog.FolderName;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ToolsPathBox.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateSource();
        DialogResult = true;
    }
}
