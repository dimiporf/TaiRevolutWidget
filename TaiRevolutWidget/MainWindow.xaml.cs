using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;
using OxyPlot.Annotations;
using OxyPlot.Wpf; // για PlotView.HideTracker()
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace TaiRevolutWidget
{
    public partial class MainWindow : Window
    {
        private readonly HttpClient _http;
        private readonly DispatcherTimer _timer;
        private readonly CultureInfo _eurCulture = CultureInfo.GetCultureInfo("el-GR");

        private const decimal AMOUNT_TAI = 30000m;
        private const decimal FEE_PCT = 1.49m;
        private const int PRICE_DECIMALS = 5;

        private LineSeries? _grossSeries;
        private LineSeries? _netSeries;
        private LineAnnotation? _cursorLine;
        private PointAnnotation? _cursorDot;

        public MainWindow()
        {
            InitializeComponent();
            _http = Api.CreateHttpClient(TimeSpan.FromSeconds(15));

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
            _timer.Tick += async (_, __) => await RefreshSummaryAsync();

            Loaded += async (_, __) =>
            {
                await RefreshSummaryAsync();
                _timer.Start();

                cmbRange.SelectedIndex = 0; // 24h
                await LoadChartAsync();
            };

            lblStatus.MouseLeftButtonUp += async (_, __) =>
            {
                if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
                {
                    try { lblStatus.Text = await CgSelfTest.PingAsync(_http); }
                    catch (Exception ex) { lblStatus.Text = "Ping error: " + ex.Message; }
                }
            };

            plot.MouseLeave += (_, __) => HideHover();
            plot.MouseMove += Plot_MouseMove;
            SizeChanged += (_, __) => HideHover();
        }

        // ===== Summary =====
        private async System.Threading.Tasks.Task RefreshSummaryAsync()
        {
            try
            {
                lblStatus.Text = "Φόρτωση τιμής…";
                var priceEur = await CoinGeckoService.GetSimplePriceEurAsync(_http);

                var gross = AMOUNT_TAI * priceEur;
                var net = gross * (1 - (FEE_PCT / 100m));

                lblPrice.Text = "€ " + FormatPrice(priceEur);
                lblGross.Text = FormatCurrency(gross);
                lblNet.Text = FormatCurrency(net);

                txtLastUpdated.Text = $"• ενημ.: {DateTime.Now:HH:mm:ss}";
                lblStatus.Text = "OK (Ctrl+Click για ping)";
            }
            catch (Exception ex)
            {
                lblStatus.Text = "Σφάλμα: " + ex.Message;
            }
        }

        private string FormatCurrency(decimal value)
            => string.Format(_eurCulture, "{0:C}", value);

        private string FormatPrice(decimal value, int decimals = PRICE_DECIMALS)
        {
            var nfi = (NumberFormatInfo)_eurCulture.NumberFormat.Clone();
            nfi.NumberDecimalDigits = decimals;
            return value.ToString("N", nfi);
        }

        private async void BtnRefresh_Click(object sender, RoutedEventArgs e) => await RefreshSummaryAsync();
        private void BtnMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) { try { DragMove(); } catch { } }

        // ===== Chart =====
        private int GetSelectedDays()
        {
            if (cmbRange.SelectedItem is ComboBoxItem item &&
                int.TryParse(item.Tag?.ToString(), out var days))
                return days;
            return 1;
        }

        private async System.Threading.Tasks.Task LoadChartAsync()
        {
            try
            {
                txtChartStatus.Text = "Φόρτωση δεδομένων…";
                int days = GetSelectedDays();

                var pricePoints = await CoinGeckoService.GetMarketChartPricesAsync(_http, days);
                var series = ToValueSeries(pricePoints, AMOUNT_TAI, FEE_PCT);

                var model = new PlotModel
                {
                    PlotAreaBorderColor = OxyColors.Gray,
                    Background = OxyColors.Transparent,
                    TextColor = OxyColors.White,
                    Title = days == 1 ? "Αξία TAI σε EUR (24h)" : $"Αξία TAI σε EUR ({days} ημέρες)"
                };

                var xAxis = new DateTimeAxis
                {
                    Position = AxisPosition.Bottom,
                    StringFormat = days == 1 ? "HH:mm" : "dd/MM",
                    IntervalType = days == 1 ? DateTimeIntervalType.Hours : DateTimeIntervalType.Days,
                    MinorIntervalType = days == 1 ? DateTimeIntervalType.Minutes : DateTimeIntervalType.Days,
                    MajorGridlineStyle = LineStyle.Solid,
                    MinorGridlineStyle = LineStyle.Dot,
                    // FromArgb χρειάζεται bytes στη δική σου έκδοση
                    MajorGridlineColor = OxyColor.FromArgb((byte)80, (byte)255, (byte)255, (byte)255),
                    MinorGridlineColor = OxyColor.FromArgb((byte)40, (byte)255, (byte)255, (byte)255)
                };
                model.Axes.Add(xAxis);

                var yAxis = new LinearAxis
                {
                    Position = AxisPosition.Left,
                    StringFormat = "€#,0.##",
                    MajorGridlineStyle = LineStyle.Solid,
                    MinorGridlineStyle = LineStyle.Dot,
                    MajorGridlineColor = OxyColor.FromArgb((byte)80, (byte)255, (byte)255, (byte)255),
                    MinorGridlineColor = OxyColor.FromArgb((byte)40, (byte)255, (byte)255, (byte)255)
                };
                model.Axes.Add(yAxis);

                _grossSeries = new LineSeries { Title = "Αξία (μικτή)", StrokeThickness = 2 };
                _netSeries = new LineSeries { Title = "Αξία με fees (καθαρή)", StrokeThickness = 2 };

                foreach (var p in series)
                {
                    double x = DateTimeAxis.ToDouble(p.Time);
                    _grossSeries.Points.Add(new DataPoint(x, (double)p.GrossValue));
                    _netSeries.Points.Add(new DataPoint(x, (double)p.NetValue));
                }

                model.Series.Add(_grossSeries);
                model.Series.Add(_netSeries);

                _cursorLine = new LineAnnotation
                {
                    Type = LineAnnotationType.Vertical,
                    Color = OxyColors.SkyBlue,
                    LineStyle = LineStyle.Solid,
                    StrokeThickness = 1.5,
                    X = double.NaN
                };
                model.Annotations.Add(_cursorLine);

                _cursorDot = new PointAnnotation
                {
                    Shape = MarkerType.Circle,
                    Fill = OxyColors.White,
                    Stroke = OxyColors.SkyBlue,
                    StrokeThickness = 1.5,
                    Size = 3.5,
                    X = double.NaN,
                    Y = double.NaN
                };
                model.Annotations.Add(_cursorDot);

                plot.Model = model;
                txtChartStatus.Text = $"OK • σημεία: {series.Count:n0}";
                HideHover();
            }
            catch (Exception ex)
            {
                txtChartStatus.Text = "Σφάλμα: " + ex.Message;
            }
        }

        private List<ValuePoint> ToValueSeries(List<CoinGeckoService.PricePoint> pricePoints, decimal amountTai, decimal feePct)
        {
            var list = new List<ValuePoint>(pricePoints.Count);
            foreach (var pp in pricePoints)
            {
                var gross = AMOUNT_TAI * pp.PriceEur;
                var net = gross * (1 - (FEE_PCT / 100m));
                list.Add(new ValuePoint { Time = pp.Time, GrossValue = gross, NetValue = net });
            }
            return list;
        }

        private class ValuePoint
        {
            public DateTime Time { get; set; }
            public decimal GrossValue { get; set; }
            public decimal NetValue { get; set; }
        }

        private async void ChartRefresh_Click(object sender, RoutedEventArgs e) => await LoadChartAsync();
        private async void cmbRange_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (IsLoaded)
                await LoadChartAsync();
        }

        // ===== Custom hover: overlay panel =====
        private void Plot_MouseMove(object sender, MouseEventArgs e)
        {
            try
            {
                if (plot?.Model == null || _grossSeries == null || _grossSeries.Points.Count == 0)
                {
                    HideHover();
                    return;
                }

                var pos = e.GetPosition(plot);
                var sp = new ScreenPoint(pos.X, pos.Y);

                var nearest = _grossSeries.GetNearestPoint(sp, interpolate: false);
                if (nearest == null)
                {
                    HideHover();
                    return;
                }

                int idx = (int)nearest.Index;
                if (idx < 0 || idx >= _grossSeries.Points.Count)
                {
                    HideHover();
                    return;
                }

                var gp = _grossSeries.Points[idx];
                DataPoint? np = null;
                if (_netSeries != null && idx < _netSeries.Points.Count)
                    np = _netSeries.Points[idx];

                // update visual markers inside plot
                if (_cursorLine != null) _cursorLine.X = gp.X;
                if (_cursorDot != null) { _cursorDot.X = gp.X; _cursorDot.Y = gp.Y; }

                // texts
                var dt = DateTimeAxis.ToDateTime(gp.X);
                var grossStr = string.Format(_eurCulture, "{0:C}", (decimal)gp.Y);
                var netStr = np.HasValue ? string.Format(_eurCulture, "{0:C}", (decimal)np.Value.Y) : "—";

                hoverHeader.Text = $"{dt:dd/MM/yyyy HH:mm}";
                hoverGross.Text = $"Μικτή: {grossStr}";
                hoverNet.Text = $"Καθαρή: {netStr}";

                // --- DataPoint -> ScreenPoint μέσω των αξόνων (ΟΧΙ Transform(gp)) ---
                Axis? xAxis = null, yAxis = null;
                foreach (var ax in plot.Model.Axes)
                {
                    if (ax.Position == AxisPosition.Bottom) xAxis = ax;
                    else if (ax.Position == AxisPosition.Left) yAxis = ax;
                }
                if (xAxis == null || yAxis == null)
                {
                    HideHover();
                    return;
                }

                double sx = xAxis.Transform(gp.X);
                double sy = yAxis.Transform(gp.Y);

                PositionHover(sx, sy);
                plot.InvalidatePlot(false);
            }
            catch
            {
                HideHover();
            }
        }

        private void PositionHover(double x, double y)
        {
            if (overlay == null || hoverPanel == null) return;

            hoverPanel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var w = hoverPanel.DesiredSize.Width;
            var h = hoverPanel.DesiredSize.Height;

            double left = x + 12.0;
            double top = y + 12.0;

            if (left + w > overlay.ActualWidth) left = x - w - 12.0;
            if (top + h > overlay.ActualHeight) top = y - h - 12.0;

            if (left < 0) left = 0;
            if (top < 0) top = 0;

            Canvas.SetLeft(hoverPanel, left);
            Canvas.SetTop(hoverPanel, top);
            hoverPanel.Visibility = Visibility.Visible;
        }

        private void HideHover()
        {
            if (_cursorLine != null) _cursorLine.X = double.NaN;
            if (_cursorDot != null) { _cursorDot.X = double.NaN; _cursorDot.Y = double.NaN; }
            if (hoverPanel != null) hoverPanel.Visibility = Visibility.Collapsed;

            plot.InvalidatePlot(false);
            plot.HideTracker(); // προληπτικά
        }
    }
}
