using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;

namespace TaiRevolutWidget
{
    internal static class Api
    {
        // ΒΑΛΕ εδώ το DEMO key σου (πρέπει να ξεκινά με "cg_demo_")
        public static string ApiKey { get; set; } = "insert your key here";

        // ΜΟΝΟ demo
        public static string Mode { get; set; } = "demo";

        // ΣΩΣΤΟΣ χαρτογραφητής:
        public static string BaseUrl => Mode == "demo"
            ? "https://api.coingecko.com/api/v3"
            : "https://pro-api.coingecko.com/api/v3";

        public static string HeaderName => Mode == "demo"
            ? "x-cg-demo-api-key"
            : "x-cg-pro-api-key";

        private static string ToAscii(string s) => new string(s.Where(c => c <= 0x7F).ToArray()).Trim();

        public static HttpClient CreateHttpClient(TimeSpan? timeout = null)
        {
            var http = new HttpClient(new HttpClientHandler { UseCookies = false })
            {
                Timeout = timeout ?? TimeSpan.FromSeconds(15)
            };

            http.DefaultRequestHeaders.UserAgent.Clear();
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("TAIWidget", "1.0"));
            http.DefaultRequestHeaders.Accept.Clear();
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var key = ToAscii(ApiKey ?? "");
            if (!string.IsNullOrWhiteSpace(key))
            {
                // καθάρισε τυχόν παλιούς headers και βάλε ΤΟΝ σωστό για demo
                http.DefaultRequestHeaders.Remove("x-cg-pro-api-key");
                http.DefaultRequestHeaders.Remove("x-cg-demo-api-key");
                http.DefaultRequestHeaders.TryAddWithoutValidation(HeaderName, key);
            }

            return http;
        }
    }
}
