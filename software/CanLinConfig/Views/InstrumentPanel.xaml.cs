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
}

public class WidgetTemplateSelector : DataTemplateSelector
{
    public DataTemplate? NumericTemplate { get; set; }
    public DataTemplate? BarTemplate { get; set; }
    public DataTemplate? GaugeTemplate { get; set; }
    public DataTemplate? BooleanTemplate { get; set; }
    public DataTemplate? EnumTemplate { get; set; }

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
            _ => NumericTemplate
        };
    }
}
