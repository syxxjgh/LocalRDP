using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DeskClone.Controls;

/// <summary>bool → Visibility（true 可见）。与 WPF 内置版本行为一致，供 XAML 以资源形式引用。</summary>
public sealed class BooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is true ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is Visibility.Visible ? true : false;
    }
}

/// <summary>bool → Visibility（true 折叠），即反转版本。</summary>
public sealed class BooleanToVisibilityRevertConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is true ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is Visibility.Visible ? false : true;
    }
}
