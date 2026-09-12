using System.Threading.Tasks;
using ClassPixelPro.Core;
using ClassPixelPro.Core.Abstractions.Automation;
using ClassPixelPro.Core.Attributes;
namespace ClassPixelPro.Services.Automation.Actions;

[ActionInfo("classpixelpro.app.quit", "退出 ClassPixelPro", "\ue0df", addDefaultToMenu:false)]
public class AppQuitAction : ActionBase
{
    protected override async Task OnInvoke()
    {
        AppBase.Current.Stop();
    }
}