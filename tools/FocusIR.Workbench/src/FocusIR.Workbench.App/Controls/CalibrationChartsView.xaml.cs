using System.Windows;
using System.Windows.Controls;
using FocusIR.Workbench.App.ViewModels;
using Microsoft.Win32;
namespace FocusIR.Workbench.App.Controls;
public partial class CalibrationChartsView : UserControl
{
    public CalibrationChartsView() {
        InitializeComponent();
        ModelPlot.NodeDragged+=(id,focus)=> { if(DataContext is CalibrationChartsViewModel vm) vm.MoveCurveNode(id,focus); };
    }
    private void EditCurve_Click(object sender,RoutedEventArgs e) { if(DataContext is CalibrationChartsViewModel vm) vm.ToggleCurveEditing(); }
    private async void SaveCurve_Click(object sender,RoutedEventArgs e) { if(DataContext is CalibrationChartsViewModel vm) await vm.SaveCurveAsync(); }
    private void Export_Click(object sender,RoutedEventArgs e)
    {
        if(DataContext is not CalibrationChartsViewModel vm || vm.TaskId is null) return;
        var dialog=new SaveFileDialog { Filter="CSV 文件 (*.csv)|*.csv", FileName=$"focusir-calibration-{DateTime.Now:yyyyMMdd-HHmmss}.csv", AddExtension=true };
        if(dialog.ShowDialog()==true) {
            try { vm.ExportCsv(dialog.FileName); vm.SetError("CSV 已导出，完整性以 record_complete 列为准。"); }
            catch(Exception ex) { vm.SetError($"导出失败：{ex.Message}"); }
        }
    }
}
