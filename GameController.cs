using Microsoft.AspNetCore.Mvc;
using System.Text;
using System.Text.Json;

namespace AITextGame.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class GameController : ControllerBase
    {
        private static readonly HttpClient client = new HttpClient();
        private readonly IConfiguration _config;
        private static readonly List<object> chatHistory = new List<object>();

        // 120 Tur sınırı için sayaç eklendi
        private static int _turnCount = 0;
        private const int MaxTurns = 120;

        public GameController(IConfiguration config)
        {
            _config = config;
        }

        [HttpPost("start")]
        public async Task<IActionResult> StartGame([FromBody] GameSetupSetup setup)
        {
            chatHistory.Clear();
            _turnCount = 1; // Oyun başladığında tur 1 olur

            string systemPrompt = $@"
Sen acımasız, gerçekçi ve profesyonel bir metin tabanlı RPG oyun yöneticisisin (Dungeon Master).
Oyuncunun seçtiği dünya kurallarına tamamen sadık kal ancak evrenin tehlikelerini ona hissettir.

Oyun Evreni: {setup.dunya}
Oyuncunun Rolü: {setup.rol}
Oyunun Teması: {setup.tema}
Ek Detaylar: {setup.detay}

KURALLAR:
1. Kısa, sürükleyici ve atmosferik bir giriş yap. Çevre betimlemelerini gerçekçi detaylarla zenginleştir.
2. Oyuncu adına ASLA karar verme. Sadece NPC'leri, çevreyi ve olayların sonuçlarını yönet.
3. Her cevabın sonunda oyuncuya ne yapmak istediğini sor.
4. GERÇEKÇİLİK VE ÖLÜM MEKANİĞİ: Bu dünyada 'Kahraman Koruması' (Plot Armor) YOKTUR. Oyuncu eğer mantıksız, dikkatsiz veya boyunu aşan bir karar verirse onu cezalandır. Gerekirse uzuv kaybetsin, kalıcı hasar alsın, tüm eşyalarını çaldırsın veya ÖLSÜN. Kötü kararların bedeli ağır olmalı.
5. HİKAYE UZUNLUĞU: Hikaye maksimum {MaxTurns} hamlede sona erecek şekilde kurgulanmalıdır. Olayların gidişatını buna göre hızlandır veya yavaşlat.
6. HER CEVABIN EN SON SATIRINDA MUTLAKA ŞU FORMATTA GÖRSEL PROMPT VER:

[IMAGE_PROMPT: detailed fantasy anime scene, cinematic lighting, epic composition]

7. IMAGE_PROMPT SADECE İNGİLİZCE OLACAK.
8. FORMATI ASLA ATLAMA.
";

            chatHistory.Add(new
            {
                role = "user",
                parts = new[] { new { text = systemPrompt + "\n\nHikayeyi başlat." } }
            });

            string tamCevap = await GeminiApiyeSor(chatHistory);

            if (tamCevap.StartsWith("[API Hatası]") || tamCevap.StartsWith("[Bağlantı Hatası]"))
            {
                return BadRequest(new { response = tamCevap, imagePrompt = "" });
            }

            chatHistory.Add(new
            {
                role = "model",
                parts = new[] { new { text = tamCevap } }
            });

            var paket = CevabiAyristir(tamCevap);
            return Ok(paket);
        }

        [HttpPost("action")]
        public async Task<IActionResult> PlayerAction([FromBody] PlayerInput input)
        {
            if (string.IsNullOrEmpty(input.Action))
                return BadRequest("Eylem boş olamaz.");

            _turnCount++; // Her hamlede turu artırıyoruz

            // Yapay zekaya arkaplanda kaçıncı turda olduğumuzu bildiriyoruz
            string actionWithContext = $"[Tur: {_turnCount}/{MaxTurns}] Oyuncu Hamlesi: {input.Action}";

            if (_turnCount >= MaxTurns)
            {
                actionWithContext += "\n[SİSTEM NOTU: Bu 120. ve son hamle. Lütfen oyuncunun bu son hamlesinin sonucuna göre hikayeyi epik, dramatik veya trajik bir şekilde tamamen sonlandır ve hikayenin bittiğini belirt.]";
            }

            chatHistory.Add(new
            {
                role = "user",
                parts = new[] { new { text = actionWithContext } }
            });

            string tamCevap = await GeminiApiyeSor(chatHistory);

            if (tamCevap.StartsWith("[API Hatası]") || tamCevap.StartsWith("[Bağlantı Hatası]"))
            {
                // Hata durumunda turu geri alıyoruz ki boşa gitmesin
                _turnCount--;
                return BadRequest(new { response = tamCevap, imagePrompt = "" });
            }

            chatHistory.Add(new
            {
                role = "model",
                parts = new[] { new { text = tamCevap } }
            });

            var paket = CevabiAyristir(tamCevap);
            return Ok(paket);
        }

        private static object CevabiAyristir(string tamCevap)
        {
            string hikaye = tamCevap;
            string gorselPrompt = "";
            const string tag = "[IMAGE_PROMPT:";

            if (tamCevap.Contains(tag))
            {
                int baslangic = tamCevap.IndexOf(tag);
                if (baslangic >= 0)
                {
                    hikaye = tamCevap.Substring(0, baslangic).Trim();
                    string kalan = tamCevap.Substring(baslangic + tag.Length);
                    int sonParantez = kalan.LastIndexOf("]");

                    if (sonParantez >= 0)
                    {
                        gorselPrompt = kalan.Substring(0, sonParantez).Trim();
                    }
                    else
                    {
                        gorselPrompt = kalan.Trim();
                    }
                }
            }

            if (hikaye.Contains(tag))
            {
                hikaye = hikaye.Substring(0, hikaye.IndexOf(tag)).Trim();
            }

            return new
            {
                response = hikaye,
                imagePrompt = gorselPrompt
            };
        }

        private async Task<string> GeminiApiyeSor(List<object> gecmis)
        {
            try
            {
                string apiKey = _config["GeminiApiKey"]!.Trim();
                string url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent?key={apiKey}";

                var requestBody = new { contents = gecmis };
                string jsonPayload = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                HttpResponseMessage response = await client.PostAsync(url, content);
                string responseString = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    using JsonDocument doc = JsonDocument.Parse(responseString);
                    return doc.RootElement
                        .GetProperty("candidates")[0]
                        .GetProperty("content")
                        .GetProperty("parts")[0]
                        .GetProperty("text")
                        .GetString()
                        ?? "Hikaye devam ettirilemedi.";
                }

                return $"[API Hatası]: {response.StatusCode} - {responseString}";
            }
            catch (Exception ex)
            {
                return $"[Bağlantı Hatası]: {ex.Message}";
            }
        }

        [HttpGet("image")]
        public async Task<IActionResult> GenerateImage([FromQuery] string prompt)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(prompt))
                    return BadRequest();

                string safePrompt = Uri.EscapeDataString(prompt);

                string imageUrl =
                    $"https://image.pollinations.ai/prompt/{safePrompt}" +
                    $"?width=768" +
                    $"&height=768" +
                    $"&seed={Random.Shared.Next(1, 999999)}";

                using HttpClient http = new HttpClient();
                http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");

                byte[] imageBytes = await http.GetByteArrayAsync(imageUrl);
                return File(imageBytes, "image/jpeg");
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }
    }

    public class GameSetupSetup
    {
        public string dunya { get; set; } = "";
        public string rol { get; set; } = "";
        public string tema { get; set; } = "";
        public string detay { get; set; } = "";
    }

    public class PlayerInput
    {
        public string Action { get; set; } = "";
    }
}