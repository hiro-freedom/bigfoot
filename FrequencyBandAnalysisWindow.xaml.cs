using System;
using System.Windows;

namespace bigfoot;

public partial class FrequencyBandAnalysisWindow : Window
{
    public FrequencyBandAnalysisWindow()
    {
        InitializeComponent();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
