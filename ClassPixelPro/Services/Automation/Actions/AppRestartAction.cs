using System.Threading.Tasks;
using ClassPixelPro.Core;
using ClassPixelPro.Core.Abstractions.Automation;
using ClassPixelPro.Core.Attributes;
using ClassPixelPro.Models.Actions;
namespace ClassPixelPro.Services.Automation.Actions;

[ActionInfo("classpixelpro.app.restart", "重启 ClassPixelPro", "\ue0bd", addDefaultToMenu:false)]
public class AppRestartAction : ActionBase<AppRestartActionSettings>
{
    protected override async Task OnInvoke()
    {
        AppBase.Current.Restart(Settings.Value);
    }
}