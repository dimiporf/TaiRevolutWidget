using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TaiRevolutWidget
{
    /// <summary>
    /// Βοηθητικό service για CoinGecko:
    /// - Dynamic resolve του coin id για TARS AI (TAI) με caching,
    /// - Ανάγνωση τρέχουσας τιμής σε EUR,
    /// - Ανάγνωση ιστορικού (market_chart) σε EUR,
    /// - Ανθεκτικό parsing όταν το API επιστρέφει διαφορετικό id από αυτό που περιμένουμε.
    /// </summary>
    internal static class CoinGeckoService
    {
        /// <summary>
        /// Αν το ορίσεις (π.χ. "tars-ai" ή "tars-protocol"), παρακάμπτει το dynamic resolver.
        /// Χρήσιμο για γρήγορο test/diagnostic.
        /// </summary>
        public static string? ForcedCoinId { get; set; } = null;

        // Cache του resolved coin id για να μη χτυπάμε διαρκώς το /search
        private static string? _resolvedId;
        private static readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

        // Γνωστοί πιθανοί αναγνωριστές στο CoinGecko
        private static readonly string[] CandidateIds = new[]
        {
            "tars-ai",        // συχνότερο id
            "tars-protocol",  // εναλλακτικό
            "tars"            // παλαιό/γενικό
        };

        /// <summary>
        /// Επιστρέφει το coin id για TARS AI (TAI). Τιμά το ForcedCoinId, αλλιώς χρησιμοποιεί dynamic + cache.
        /// </summary>
        public static async Task<string> GetTaiIdAsync(HttpClient http, CancellationToken ct = default)
        {
            if (!string.IsNullOrWhiteSpace(ForcedCoinId))
                return ForcedCoinId!;

            if (_resolvedId != null) return _resolvedId;

            await _gate.WaitAsync(ct);
            try
            {
                if (_resolvedId != null) return _resolvedId;

                // 1) Προσπάθεια μέσω /search?query=tai
                var searchUrl = $"{Api.BaseUrl}/search?query=tai";
                using (var resp = await http.GetAsync(searchUrl, ct))
                {
                    var body = await resp.Content.ReadAsStringAsync(ct);
                    if (resp.IsSuccessStatusCode)
                    {
                        using var doc = JsonDocument.Parse(body);
                        if (doc.RootElement.TryGetProperty("coins", out var coins))
                        {
                            // Προτιμάμε symbol = "tai" & name να περιέχει "tars"
                            var match = coins.EnumerateArray()
                                             .Select(e => new
                                             {
                                                 id = e.TryGetProperty("id", out var idEl) ? idEl.GetString() : null,
                                                 symbol = e.TryGetProperty("symbol", out var sEl) ? sEl.GetString() : null,
                                                 name = e.TryGetProperty("name", out var nEl) ? nEl.GetString() : null
                                             })
                                             .FirstOrDefault(c =>
                                                 (c.symbol ?? "").Equals("tai", StringComparison.OrdinalIgnoreCase) &&
                                                 (c.name ?? "").IndexOf("tars", StringComparison.OrdinalIgnoreCase) >= 0);

                            if (match?.id is string ok)
                            {
                                _resolvedId = ok;
                                return _resolvedId;
                            }

                            // Διαφορετικά: πάρε το πρώτο με symbol=tai
                            var anyTai = coins.EnumerateArray()
                                              .Select(e => new
                                              {
                                                  id = e.TryGetProperty("id", out var idEl) ? idEl.GetString() : null,
                                                  symbol = e.TryGetProperty("symbol", out var sEl) ? sEl.GetString() : null
                                              })
                                              .FirstOrDefault(c =>
                                                  (c.symbol ?? "").Equals("tai", StringComparison.OrdinalIgnoreCase));

                            if (anyTai?.id is string ok2)
                            {
                                _resolvedId = ok2;
                                return _resolvedId;
                            }
                        }
                    }
                    // Αν αποτύχει το /search, συνεχίζουμε με fallback candidates
                }

                // 2) Έλεγχος ύπαρξης για γνωστά ids
                foreach (var id in CandidateIds)
                {
                    if (await CoinExistsAsync(http, id, ct))
                    {
                        _resolvedId = id;
                        return _resolvedId;
                    }
                }

                // 3) Τελευταίο fallback
                _resolvedId = CandidateIds.First();
                return _resolvedId;
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// Επιστρέφει την τρέχουσα τιμή σε EUR για το TAI, με ανθεκτικό parsing.
        /// </summary>
        public static async Task<decimal> GetSimplePriceEurAsync(HttpClient http, CancellationToken ct = default)
        {
            var expectedId = await GetTaiIdAsync(http, ct);
            var url = $"{Api.BaseUrl}/simple/price?ids={Uri.EscapeDataString(expectedId)}&vs_currencies=eur";

            using var resp = await http.GetAsync(url, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            EnsureSuccess(resp, body);

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            // 1) Κανονικά περιμένουμε το expectedId
            if (root.TryGetProperty(expectedId, out var idNode) && idNode.TryGetProperty("eur", out var eurNode))
                return eurNode.GetDecimal();

            // 2) Κάποια installs επιστρέφουν άλλο id (alias). Πάρ’το από το πρώτο property.
            if (root.ValueKind == JsonValueKind.Object)
            {
                var props = root.EnumerateObject().ToList();
                if (props.Count == 1 && props[0].Value.TryGetProperty("eur", out var eur2))
                    return eur2.GetDecimal();

                // 3) Αν υπάρχουν πολλαπλά keys, δοκίμασε όποιο έχει "eur"
                foreach (var p in props)
                {
                    if (p.Value.TryGetProperty("eur", out var eur3))
                        return eur3.GetDecimal();
                }

                // 4) Διάγνωση: ποιες ιδιότητες επιστράφηκαν;
                var keys = string.Join(", ", props.Select(p => p.Name));
                throw new Exception($"'/simple/price' δεν επέστρεψε key '{expectedId}'. Keys: [{keys}] • Body: {body}");
            }

            throw new Exception($"Αναπάντεχη δομή JSON στο '/simple/price'. Body: {body}");
        }

        /// <summary>
        /// Επιστρέφει ιστορικές τιμές (EUR) για {days}. 24h χωρίς interval, 7/30d = daily.
        /// </summary>
        public static async Task<List<PricePoint>> GetMarketChartPricesAsync(
            HttpClient http, int days, CancellationToken ct = default)
        {
            var coinId = await GetTaiIdAsync(http, ct);
            var intervalPart = days <= 1 ? "" : "&interval=daily";
            var url = $"{Api.BaseUrl}/coins/{Uri.EscapeDataString(coinId)}/market_chart?vs_currency=eur&days={days}{intervalPart}";

            using var resp = await http.GetAsync(url, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            EnsureSuccess(resp, body);

            using var doc = JsonDocument.Parse(body);
            var prices = doc.RootElement.GetProperty("prices");

            var list = new List<PricePoint>(prices.GetArrayLength());
            foreach (var el in prices.EnumerateArray())
            {
                var tsMs = el[0].GetDouble();
                var price = el[1].GetDecimal();
                var time = DateTimeOffset.FromUnixTimeMilliseconds((long)tsMs).LocalDateTime;

                list.Add(new PricePoint { Time = time, PriceEur = price });
            }
            return list;
        }

        // ===== Helpers =======================================================

        private static async Task<bool> CoinExistsAsync(HttpClient http, string id, CancellationToken ct)
        {
            var url = $"{Api.BaseUrl}/coins/{Uri.EscapeDataString(id)}" +
                      "?localization=false&tickers=false&market_data=false&community_data=false&developer_data=false&sparkline=false";

            using var resp = await http.GetAsync(url, ct);
            return resp.IsSuccessStatusCode;
        }

        private static void EnsureSuccess(HttpResponseMessage resp, string body)
        {
            if (!resp.IsSuccessStatusCode)
                throw new Exception($"HTTP {(int)resp.StatusCode}: {resp.ReasonPhrase} • {body}");
        }

        // ===== Models ========================================================

        public class PricePoint
        {
            public DateTime Time { get; set; }
            public decimal PriceEur { get; set; }
        }
    }
}
