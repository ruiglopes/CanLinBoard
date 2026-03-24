using System.Windows;
using System.Windows.Controls;
using CanLinConfig.Models;
using CanLinConfig.ViewModels;

namespace CanLinConfig.Views;

public partial class InstrumentPanel : UserControl
{
    public InstrumentPanel()
    {
        InitializeComponent();
    }

    private void OnChangeType(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi || mi.DataContext is not InstrumentWidget widget) return;
        if (!Enum.TryParse<WidgetType>(mi.Tag?.ToString(), out var newType)) return;

        if (DataContext is InstrumentPanelViewModel vm)
        {
            vm.SetWidgetType(widget, newType);
            if (newType == WidgetType.BitPanel && widget.BitLabels.Count == 0)
                ShowBitPanelConfig(widget);
        }
    }

    private void OnEditBitPanel(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi || mi.DataContext is not InstrumentWidget widget) return;
        ShowBitPanelConfig(widget);
    }

    private void ShowBitPanelConfig(InstrumentWidget widget)
    {
        // Placeholder — implemented in Task 3
    }
}

public class WidgetTemplateSelector : DataTemplateSelector
{
    public DataTemplate? NumericTemplate { get; set; }
    public DataTemplate? BarTemplate { get; set; }
    public DataTemplate? GaugeTemplate { get; set; }
    public DataTemplate? BooleanTemplate { get; set; }
    public DataTemplate? EnumTemplate { get; set; }
    public DataTemplate? BitPanelTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
    {
        if (item is not InstrumentWidget w) return base.SelectTemplate(item, container);
        return w.Type switch
        {
            WidgetType.Numeric => NumericTemplate,
            WidgetType.Bar => BarTemplate,
            WidgetType.Gauge => GaugeTemplate,
            WidgetType.Boolean => BooleanTemplate,
            WidgetType.Enum => EnumTemplate,
            WidgetType.BitPanel => BitPanelTemplate,
            _ => NumericTemplate
        };
    }
}
