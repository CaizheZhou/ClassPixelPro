using System.Windows;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml.Templates;

namespace ClassPixelPro.Core.Controls;

/// <summary>
/// 开发构建装饰层
/// </summary>
public class DevelopmentBuildAdorner : TemplatedControl
{
    /// <summary>
    /// 是否是开发构建
    /// </summary>
    public bool IsDevelopmentBuild { get; }

    /// <inheritdoc />
    public DevelopmentBuildAdorner(bool isDevelopmentBuild)
    {
        IsDevelopmentBuild = isDevelopmentBuild;
        ClipToBounds = false;
    }
}
