using System.Globalization;
using System.Windows;
using System.Windows.Data;
using MahApps.Metro.Controls;
using CanLinConfig.ViewModels;

namespace CanLinConfig.Views;

public partial class ProfileEditorWindow : MetroWindow
{
    private readonly ProfileEditorViewModel _vm;

    public ProfileDefinition Profile { get; private set; }

    public ProfileEditorWindow(ProfileDefinition profile, bool isNew)
    {
        InitializeComponent();
        _vm = new ProfileEditorViewModel(profile, isNew);
        DataContext = _vm;
        Profile = profile;
        Title = isNew ? "New Profile" : $"Edit Profile: {profile.Name}";
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_vm.ProfileName))
        {
            MessageBox.Show("Profile name is required.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (string.IsNullOrWhiteSpace(_vm.ProfileId))
        {
            MessageBox.Show("Profile ID is required.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Profile = _vm.BuildProfile();
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}

// Converter: inverse bool to Visibility
public class InverseBoolToVisConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility.Collapsed;
}

// Converter: null to Collapsed, non-null to Visible
public class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value != null ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
