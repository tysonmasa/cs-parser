using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using SDGEAutomation.Services; 
using Forms = System.Windows.Forms;
using IO = System.IO;



namespace SDGEAutomation;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>

 
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DownloadFolderBox.Text = IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads",
            "SDGE Bills");
    }

    private readonly PlaywrightService _playwright = new();

    private void DownloadScopeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // WPF raises the initial selection event while the XAML controls are still
        // being created, before these named panels have been assigned.
        if (PropertyPanel == null || SelectedPropertiesPanel == null ||
            DownloadScopeBox.SelectedItem is not ComboBoxItem selectedItem)
            return;

        var scope = selectedItem.Tag?.ToString();
        PropertyPanel.Visibility = scope == "Single" ? Visibility.Visible : Visibility.Collapsed;
        SelectedPropertiesPanel.Visibility = scope == "Selected" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BrowseFolderButton_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "Choose where to save the SDG&E bill PDF",
            InitialDirectory = IO.Directory.Exists(DownloadFolderBox.Text)
                ? DownloadFolderBox.Text
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            UseDescriptionForTitle = true
        };

        if (dialog.ShowDialog() == Forms.DialogResult.OK)
            DownloadFolderBox.Text = dialog.SelectedPath;
    }

    protected override async void OnClosed(EventArgs e)
    {
        await _playwright.CloseAsync();
        base.OnClosed(e);
    }

    private async void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        LoginButton.IsEnabled = false;

        try
        {
            if (DownloadScopeBox.SelectedItem is not ComboBoxItem selectedScope)
                throw new InvalidOperationException("Choose a download scope.");

            var scope = selectedScope.Tag?.ToString();
            var requestedAddresses = scope == "Selected"
                ? SelectedPropertiesBox.Text
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray()
                : Array.Empty<string>();

            if (scope == "Selected" && requestedAddresses.Length == 0)
                throw new ArgumentException("Enter one or more property addresses, separated by commas.");

            StatusText.Text = "Launching browser...";
            await _playwright.LaunchAsync();

            StatusText.Text = "Opening MyEnergyCenter...";
            await _playwright.GoToLoginPageAsync();

            StatusText.Text = "Logging in...";
            await _playwright.LoginAsync(UsernameBox.Text, PasswordBox.Password);
            var addresses = scope switch
            {
                "Single" => new[] { await _playwright.SelectPropertyAsync(PropertyBox.Text) },
                "All" => await _playwright.GetPropertyAddressesAsync(),
                "Selected" => requestedAddresses,
                _ => throw new InvalidOperationException("Unknown download scope.")
            };

            if (addresses.Count == 0)
                throw new Exception("No properties were found in this account.");

            var downloadedCount = 0;
            var skippedCount = 0;

            for (var index = 0; index < addresses.Count; index++)
            {
                var selectedAddress = await _playwright.SelectPropertyAsync(addresses[index]);
                StatusText.Text = $"Downloading bill {index + 1} of {addresses.Count}: {selectedAddress}";

                try
                {
                    await _playwright.DownloadLatestBillAsync(DownloadFolderBox.Text, selectedAddress);
                    downloadedCount++;
                }
                catch (NoBillAvailableException)
                {
                    skippedCount++;
                }
            }

            StatusText.Text = $"Downloaded {downloadedCount} bill(s); skipped {skippedCount} with no available bill.";

             
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
           
        }
        finally
        {
            LoginButton.IsEnabled = true;
        }
    }
}
