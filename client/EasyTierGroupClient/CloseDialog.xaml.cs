using System.Windows;

namespace EasyTierGroupClient;

public enum CloseChoice
{
    None,
    Exit,
    Hide,
}

public partial class CloseDialog : Window
{
    public CloseChoice Choice { get; private set; } = CloseChoice.None;
    public bool Remember => ChkRemember.IsChecked == true;

    public CloseDialog()
    {
        InitializeComponent();
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        Choice = RbExit.IsChecked == true ? CloseChoice.Exit : CloseChoice.Hide;
        DialogResult = true;
    }
}
