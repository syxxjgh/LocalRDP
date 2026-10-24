// 项目同时启用 WPF 与 WinForms（RDP ActiveX 宿主是 WinForms 控件），
// 两套全局 using 会引入大量同名类型。这里用全局别名统一到 WPF 侧，
// 需要 WinForms 类型的文件（如 RdpActiveXHost）自行显式 using。

global using Application = System.Windows.Application;
global using Brush = System.Windows.Media.Brush;
global using Brushes = System.Windows.Media.Brushes;
global using MessageBox = System.Windows.MessageBox;
global using MessageBoxButton = System.Windows.MessageBoxButton;
global using MessageBoxImage = System.Windows.MessageBoxImage;
global using MessageBoxResult = System.Windows.MessageBoxResult;
global using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
