using ClassPixelPro.Core.Abstractions.Controls;
using ClassPixelPro.Models.Actions;
namespace ClassPixelPro.Controls.ActionSettingsControls;

public partial class AppRestartActionSettingsControl : ActionSettingsControlBase<AppRestartActionSettings>
{
    public AppRestartActionSettingsControl()
    {
        InitializeComponent();
        DataContext = this;
    }
}
