using ClassPixelPro.Core.Abstractions.Controls;

namespace ClassPixelPro.Core.Attributes;

/// <summary>
/// 表示当前设置页面 <see cref="SettingsPageBase"/> 使用全宽度的界面。
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public class FullWidthPageAttribute : Attribute;