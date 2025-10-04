using System;
using System.Net.Http;
using System.Threading.Tasks;

namespace TaiRevolutWidget
{
    internal static class CgSelfTest
    {
        // Κάνει μια απλή κλήση /ping και επιστρέφει λεπτομέρειες για debug
        public static async Task<string> PingAsync(HttpClient http)
        {
            var url = $"{Api.BaseUrl}/ping";
            using var resp = await http.GetAsync(url);
            var body = await resp.Content.ReadAsStringAsync();
            return $"Base: {Api.BaseUrl}\nHeader: {Api.HeaderName}\nStatus: {(int)resp.StatusCode} {resp.ReasonPhrase}\nBody: {body}";
        }
    }
}
