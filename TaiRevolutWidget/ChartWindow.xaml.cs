using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;

namespace TaiRevolutWidget
{
    public partial class ChartWindow : Window
    {
        private readonly decimal _amountTai;
        private readonly decimal _feePct;
        private readonly HttpClient _http;
        private readonly CultureInfo _eurCulture = CultureInfo.GetCultureInfo("el-GR");

        public ChartWindow(decimal amountTai, decimal feePct)
        {
            InitializeComponent();
            _amountTai = amountTai;
            _feePct = feePct;
            _http = Api.CreateHttpClient(TimeSpan.FromSeconds(20));

            Loaded += async (_, __) => await LoadAndRenderAsync();
        }

        private int GetSelectedDays()
        {
            if (cmbRange.SelectedItem is System.Windows.Controls.ComboBoxItem item &&
                int.TryParse(item.Tag?.ToString(), out var days))
            {
                return days;
            }
            return 1;
        }

        private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAndRenderAsync();

        private async Task LoadAndRenderAsync()
        {
            try
            {
                txtStatus.Text = "Φόρτωση δεδομένων…";
                int days = GetSelectedDays();

                var series = await GetMarketChartValueSeriesAsync(days);

                var model = new PlotModel
                {
                    PlotAreaBorderColor = OxyColor.FromRgb(58, 65, 80),
                    Background = OxyColor.FromRgb(27, 30, 36),
                    TextColor = OxyColor.FromRgb(237, 237, 237),
                    Title = days == 1 ? "Αξία TAI σε EUR (24h)" : $"Αξία TAI σε EUR ({days} ημέρες)"
                };

                var xAxis = new DateTimeAxis
                {
                    Position = AxisPosition.Bottom,
                    StringFormat = days == 1 ? "HH:mm" : "dd/MM",
                    IntervalType = days == 1 ? DateTimeIntervalType.Hours : DateTimeIntervalType.Days,
                    MinorIntervalType = days == 1 ? DateTimeIntervalType.Minutes : DateTimeIntervalType.Days,
                    AxislineColor = OxyColors.Gray,
                    MajorGridlineStyle = LineStyle.Solid,
                    MinorGridlineStyle = LineStyle.Dot,
                    MajorGridlineColor = OxyColor.FromAColor(40, OxyColors.White),
                    MinorGridlineColor = OxyColor.FromAColor(20, OxyColors.White)
                };
                model.Axes.Add(xAxis);

                var yAxis = new LinearAxis
                {
                    Position = AxisPosition.Left,
                    StringFormat = "€#,0.##",
                    AxislineColor = OxyColors.Gray,
                    MajorGridlineStyle = LineStyle.Solid,
                    MinorGridlineStyle = LineStyle.Dot,
                    MajorGridlineColor = OxyColor.FromAColor(40, OxyColors.White),
                    MinorGridlineColor = OxyColor.FromAColor(20, OxyColors.White)
                };
                model.Axes.Add(yAxis);

                var grossSeries = new LineSeries { Title = "Αξία (μικτή)", StrokeThickness = 2 };
                var netSeries = new LineSeries { Title = "Αξία με fees (καθαρή)", StrokeThickness = 2 };

                foreach (var p in series)
                {
                    double x = DateTimeAxis.ToDouble(p.Time);
                    grossSeries.Points.Add(new DataPoint(x, (double)p.GrossValue));
                    netSeries.Points.Add(new DataPoint(x, (double)p.NetValue));
                }

                model.Series.Add(grossSeries);
                model.Series.Add(netSeries);

                plot.Model = model;
                txtStatus.Text = $"OK • σημεία: {series.Count:n0}";
            }
            catch (HttpRequestException ex)
            {
                txtStatus.Text = "Δικτυακό σφάλμα: " + ex.Message;
            }
            catch (Exception ex)
            {
                txtStatus.Text = "Σφάλμα: " + ex.Message;
            }
        }

        private async Task<List<ValuePoint>> GetMarketChartValueSeriesAsync(int days)
        {
            var list = new List<ValuePoint>();

            // resolve id
            var coinId = await CoinGeckoService.GetTaiIdAsync(_http);

            // interval κανόνας: 24h -> (χωρίς), 7/30d -> daily
            string url = days <= 1
                ? $"{Api.BaseUrl}/coins/{Uri.EscapeDataString(coinId)}/market_chart?vs_currency=eur&days={days}"
                : $"{Api.BaseUrl}/coins/{Uri.EscapeDataString(coinId)}/market_chart?vs_currency=eur&days={days}&interval=daily";

            using var resp = await _http.GetAsync(url);
            string body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
                throw new Exception($"HTTP {(int)resp.StatusCode}: {resp.ReasonPhrase} • {body}");

            using var doc = JsonDocument.Parse(body);
            var prices = doc.RootElement.GetProperty("prices");

            list.Capacity = prices.GetArrayLength();

            foreach (var el in prices.EnumerateArray())
            {
                var tsMs = el[0].GetDouble();     // Unix ms
                var price = el[1].GetDecimal();   // EUR

                var time = DateTimeOffset.FromUnixTimeMilliseconds((long)tsMs).LocalDateTime;
                var gross = _amountTai * price;
                var net = gross * (1 - (_feePct / 100m));

                list.Add(new ValuePoint { Time = time, GrossValue = gross, NetValue = net });
            }
            return list;
        }

        private class ValuePoint
        {
            public DateTime Time { get; set; }
            public decimal GrossValue { get; set; }
            public decimal NetValue { get; set; }
        }
    }
}
