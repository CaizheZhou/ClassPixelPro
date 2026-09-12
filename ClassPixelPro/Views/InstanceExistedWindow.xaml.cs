#if false
using ClassPixelPro.Core.Controls;
using System.Windows;

namespace ClassPixelPro.Views
{
    /// <summary>
    /// InstanceExistedWindow.xaml 的交互逻辑
    /// </summary>
    public partial class InstanceExistedWindow : MyWindow
    {
        public InstanceExistedWindow()
        {
            InitializeComponent();
        }

        private void ButtonRestart_OnClick(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void ButtonExit_OnClick(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}

#endif
