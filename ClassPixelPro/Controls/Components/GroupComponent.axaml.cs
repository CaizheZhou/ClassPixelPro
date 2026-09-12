using ClassPixelPro.Core.Abstractions.Controls;
using ClassPixelPro.Core.Attributes;
using ClassPixelPro.Models.ComponentSettings;

namespace ClassPixelPro.Controls.Components;

/// <summary>
/// GroupComponent.xaml 的交互逻辑
/// </summary>
[ContainerComponent]
[ComponentInfo("C911D762-107F-40C6-84CC-0146AB3C86B1", "分组容器", "\ue92f", "将多个组件组合到一个组件中显示。")]
public partial class GroupComponent : ComponentBase<GroupComponentSettings>
{
    public GroupComponent()
    {
        InitializeComponent();
    }
}
