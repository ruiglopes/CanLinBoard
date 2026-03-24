using System.Windows.Controls;
using CanLinConfig.ViewModels;

namespace CanLinConfig.Views;

public partial class SignalPanel : UserControl
{
    public SignalPanel()
    {
        InitializeComponent();

        var menu = new ContextMenu();
        var addToGraph = new MenuItem { Header = "Add to Graph" };
        addToGraph.Click += (_, _) =>
        {
            if (DataContext is SignalPanelViewModel vm && SignalGrid.SelectedItem is SignalEntry entry)
                vm.AddToGraphCommand.Execute(entry);
        };
        menu.Items.Add(addToGraph);

        var addToInstrument = new MenuItem { Header = "Add to Instrument Panel" };
        addToInstrument.Click += (_, _) =>
        {
            if (DataContext is SignalPanelViewModel vm && SignalGrid.SelectedItem is SignalEntry entry)
                vm.AddToInstrumentPanelCommand.Execute(entry);
        };
        menu.Items.Add(addToInstrument);

        SignalGrid.ContextMenu = menu;
    }
}
