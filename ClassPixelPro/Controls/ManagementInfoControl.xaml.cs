#if false
using System.Windows.Controls;
using ClassPixelPro.Core.Abstractions.Services.Management;

namespace ClassPixelPro.Controls;

/// <summary>
/// ManagementInfoControl.xaml 的交互逻辑
/// </summary>
public partial class ManagementInfoControl : UserControl
{
    public ManagementInfoControl()
    {
        InitializeComponent();
    }

    public string ManagementOrganization => App.GetService<IManagementService>().Manifest.OrganizationName;

    public IManagementService ManagementService { get; } = App.GetService<IManagementService>();
}
#endif
