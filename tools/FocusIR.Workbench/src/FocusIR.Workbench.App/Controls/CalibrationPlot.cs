using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
namespace FocusIR.Workbench.App.Controls;

public sealed record PlotPoint(double X, double Y, string Series, string Detail, bool Invalid = false, bool Line = false, string? NodeId = null);

/// <summary>绘制曲线并处理视窗操作；显式编辑模式下发送节点FOCUS拖动事件。</summary>
public sealed class CalibrationPlot : FrameworkElement
{
    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(nameof(Points), typeof(IReadOnlyList<PlotPoint>), typeof(CalibrationPlot),
        new FrameworkPropertyMetadata(Array.Empty<PlotPoint>(), FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty AxisLabelsProperty = DependencyProperty.Register(nameof(AxisLabels), typeof(string), typeof(CalibrationPlot),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty EditNodesEnabledProperty = DependencyProperty.Register(nameof(EditNodesEnabled),typeof(bool),typeof(CalibrationPlot),
        new FrameworkPropertyMetadata(false,FrameworkPropertyMetadataOptions.AffectsRender,(d,e)=>((CalibrationPlot)d).EditModeChanged((bool)e.NewValue)));
    public bool EditNodesEnabled { get=>(bool)GetValue(EditNodesEnabledProperty);set=>SetValue(EditNodesEnabledProperty,value); }
    public event Action<string,double>? NodeDragged;
    private string? _editingNode;
    private bool _freezeBounds;
    private double _frozenMinX,_frozenMaxX,_frozenMinY,_frozenMaxY;
    private (double X,double Y,double Scale) _beforeEditView;
    private void EditModeChanged(bool enabled) {
        _editingNode=null;_drag=null;ReleaseMouseCapture();
        if(enabled) {
            _beforeEditView=(_viewX,_viewY,_scale);
            var p=Points.Where(v=>!v.Invalid&&double.IsFinite(v.X)&&double.IsFinite(v.Y)).ToArray();
            if(p.Length>0) {_frozenMinX=p.Min(v=>v.X);_frozenMaxX=p.Max(v=>v.X);_frozenMinY=p.Min(v=>v.Y);_frozenMaxY=p.Max(v=>v.Y);_freezeBounds=true;}
        }
        else {_freezeBounds=false;(_viewX,_viewY,_scale)=_beforeEditView;}
        InvalidateVisual();
    }
    public IReadOnlyList<PlotPoint> Points { get => (IReadOnlyList<PlotPoint>)GetValue(PointsProperty); set => SetValue(PointsProperty, value); }
    public string AxisLabels { get => (string)GetValue(AxisLabelsProperty); set => SetValue(AxisLabelsProperty, value); }
    private double _viewX = 0, _viewY = 0, _scale = 1;
    private Point? _drag;
    private Rect _plot;
    private double _minX, _minY, _spanX = 1, _spanY = 1;
    private static readonly Brush[] Palette = [Brushes.SteelBlue, Brushes.DarkOrange, Brushes.SeaGreen, Brushes.DarkViolet, Brushes.Crimson, Brushes.Teal, Brushes.SaddleBrown];
    public CalibrationPlot() { ClipToBounds = true; Focusable = true; }
    public void ResetView() { _viewX = _viewY = 0; _scale = 1; InvalidateVisual(); }
    private void Label(DrawingContext d, string text, double x, double y, Brush? color = null) =>
        d.DrawText(new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Microsoft YaHei UI"), 11, color ?? Brushes.SlateGray, VisualTreeHelper.GetDpi(this).PixelsPerDip), new Point(x,y));
    protected override void OnRender(DrawingContext d)
    {
        base.OnRender(d);
        d.DrawRectangle(Brushes.White, null, new Rect(RenderSize));
        _plot = new Rect(65,32,Math.Max(1,ActualWidth-85),Math.Max(1,ActualHeight-105));
        var values = Points.Where(p => double.IsFinite(p.X) && double.IsFinite(p.Y)).ToArray();
        if (values.Length == 0) { Label(d,"标定开始后显示设备曲线；滚轮缩放、拖动平移、双击复位。",12,12); return; }
        var valid = values.Where(p=>!p.Invalid).ToArray();
        if (valid.Length == 0) valid = values;
        var minX=_freezeBounds?_frozenMinX:valid.Min(p=>p.X); var maxX=_freezeBounds?_frozenMaxX:valid.Max(p=>p.X);
        var minY=_freezeBounds?_frozenMinY:valid.Min(p=>p.Y); var maxY=_freezeBounds?_frozenMaxY:valid.Max(p=>p.Y);
        var dx=Math.Max(1,maxX-minX); var dy=Math.Max(0.001,maxY-minY);
        _minX=minX-0.03*dx+_viewX*dx; _spanX=1.06*dx/_scale;
        _minY=minY-0.03*dy+_viewY*dy; _spanY=1.06*dy/_scale;
        for(var i=0;i<=4;i++) {
            var x=_plot.Left+i*_plot.Width/4; var y=_plot.Bottom-i*_plot.Height/4;
            d.DrawLine(new Pen(Brushes.Gainsboro,0.6),new Point(x,_plot.Top),new Point(x,_plot.Bottom));
            d.DrawLine(new Pen(Brushes.Gainsboro,0.6),new Point(_plot.Left,y),new Point(_plot.Right,y));
            Label(d,(_minX+i*_spanX/4).ToString("0.##",CultureInfo.InvariantCulture),x-16,_plot.Bottom+4);
            Label(d,(_minY+i*_spanY/4).ToString("0.###",CultureInfo.InvariantCulture),2,y-6);
        }
        Label(d,AxisLabels,65,3);
        d.PushClip(new RectangleGeometry(_plot));
        int groupIndex=0;
        foreach(var group in values.GroupBy(p=>p.Series)) {
            var brush=Palette[groupIndex++%Palette.Length]; Point? last=null;
            foreach(var p in group) {
                var point=Map(p.X,p.Invalid ? _minY : p.Y);
                if (p.Line && last is { } from) d.DrawLine(new Pen(brush,1.2),from,point);
                if(p.Invalid) {
                    d.DrawLine(new Pen(Brushes.Red,1.2),new Point(point.X-3,point.Y-3),new Point(point.X+3,point.Y+3));
                    d.DrawLine(new Pen(Brushes.Red,1.2),new Point(point.X-3,point.Y+3),new Point(point.X+3,point.Y-3));
                } else if(!p.Line) d.DrawEllipse(brush,null,point,EditNodesEnabled&&p.NodeId is not null?5:2.2,EditNodesEnabled&&p.NodeId is not null?5:2.2);
                last=p.Invalid?null:point;
            }
        }
        d.Pop();
        groupIndex=0; double legendX=65,legendY=_plot.Bottom+28;
        foreach(var series in values.Select(p=>p.Series).Distinct()) {
            if(legendX+series.Length*12>ActualWidth-12) { legendX=65; legendY+=16; }
            var brush=values.Where(p=>p.Series==series).All(p=>p.Invalid)?Brushes.Red:Palette[groupIndex%Palette.Length]; groupIndex++;
            Label(d,series,legendX,legendY,brush); legendX+=series.Length*12+18;
        }
    }
    private Point Map(double x,double y) => new(_plot.Left+(x-_minX)/_spanX*_plot.Width,_plot.Bottom-(y-_minY)/_spanY*_plot.Height);
    protected override void OnMouseWheel(MouseWheelEventArgs e) {
        if(_editingNode is not null) {e.Handled=true;return;}
        var factor=e.Delta>0?1.25:0.8; var next=Math.Clamp(_scale*factor,1,128);
        var p=e.GetPosition(this); double dx=(p.X-_plot.Left)/Math.Max(1,_plot.Width),dy=(_plot.Bottom-p.Y)/Math.Max(1,_plot.Height);
        _viewX+=dx*(1.06/_scale-1.06/next); _viewY+=dy*(1.06/_scale-1.06/next); _scale=next;
        InvalidateVisual(); e.Handled=true;
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e) {
        Focus();
        if(EditNodesEnabled) {
            var mouse=e.GetPosition(this);
            var node=Points.Where(v=>v.NodeId is not null && !v.Line && !v.Invalid)
                .OrderBy(v=>(Map(v.X,v.Y)-mouse).LengthSquared).FirstOrDefault();
            if(node is not null && (Map(node.X,node.Y)-mouse).LengthSquared<=144) {_editingNode=node.NodeId;CaptureMouse();e.Handled=true;}
            return;
        }
        if(e.ClickCount==2) {ResetView();return;}
        _drag=e.GetPosition(this);CaptureMouse();
    }
    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e) {
        if(EditNodesEnabled) {_drag=e.GetPosition(this);CaptureMouse();e.Handled=true;}
    }
    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e) { _drag=null;ReleaseMouseCapture(); }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { _editingNode=null;_drag=null;ReleaseMouseCapture(); }
    protected override void OnLostMouseCapture(MouseEventArgs e) { _editingNode=null;_drag=null;base.OnLostMouseCapture(e); }
    protected override void OnMouseMove(MouseEventArgs e) {
        var p=e.GetPosition(this);
        if(_editingNode is { } id && IsMouseCaptured && EditNodesEnabled) {
            double focus=_minY+(_plot.Bottom-p.Y)/Math.Max(1,_plot.Height)*_spanY;
            NodeDragged?.Invoke(id,focus);e.Handled=true;
        }
        else if(_drag is { } previous && IsMouseCaptured) {
            _viewX-=(p.X-previous.X)/Math.Max(1,_plot.Width)*1.06/_scale;
            _viewY+=(p.Y-previous.Y)/Math.Max(1,_plot.Height)*1.06/_scale;_drag=p;InvalidateVisual();
        }
        else if(Points.Count>0) {
            var nearest=Points.Where(v=>double.IsFinite(v.X)&&double.IsFinite(v.Y) && (!EditNodesEnabled || v.NodeId is not null)).MinBy(v=>(Map(v.X,v.Invalid?_minY:v.Y)-p).LengthSquared);
            ToolTip=nearest?.Detail;
            Cursor=EditNodesEnabled && nearest?.NodeId is not null && (Map(nearest.X,nearest.Y)-p).LengthSquared<=144 ? Cursors.SizeNS : Cursors.Arrow;
        }
    }
}
