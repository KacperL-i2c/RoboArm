using System.Windows.Controls;
using System.Windows.Input;
using RoboArm.App.ViewModels;

namespace RoboArm.App.Views;

public partial class AxisCard : UserControl
{
    private AxisCardViewModel Model => (AxisCardViewModel)DataContext;

    public AxisCard()
    {
        InitializeComponent();
    }

    private void OnJogMinusDown(object sender, MouseButtonEventArgs e) => Model.JogMinusStart.Execute(null);

    private void OnJogPlusDown(object sender, MouseButtonEventArgs e) => Model.JogPlusStart.Execute(null);

    private void OnJogUp(object sender, MouseEventArgs e) => Model.JogStopCommand.Execute(null);
}
