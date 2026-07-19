using Microsoft.AspNetCore.Mvc;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace AITextGame.Controllers;

[ApiController]
[Route("api/[controller]")]
public class GameController : ControllerBase
{
    private const int MaxTurns = 120;
    private static readonly ConcurrentDictionary<Guid, GameState> Games = new();
    private readonly IConfiguration _config;
    private readonly IHttpClientFactory _httpClientFactory;

    public GameController(IConfiguration config, IHttpClientFactory httpClientFactory)
    {
        _config = config;
        _httpClientFactory = httpClientFactory;
    }

    [HttpPost("start")]
    public async Task<IActionResult> StartGame([FromBody] GameSetup setup)
    {
        if (string.IsNullOrWhiteSpace(setup.Dunya) || string.IsNullOrWhiteSpace(setup.Rol) || string.IsNullOrWhiteSpace(setup.Tema))
            return BadRequest(new { error = "Dünya, rol ve tema zorunludur." });

        RemoveExpiredGames();
        var gameId = Guid.NewGuid();
        var state = new GameState { TurnCount = 1 };
        Games[gameId] = state;

        var systemPrompt = $"""
            Sen gerçekçi ve profesyonel bir metin tabanlı RPG oyun yöneticisisin.
            Oyun Evreni: {setup.Dunya}
            Oyuncunun Rolü: {setup.Rol}
            Oyunun Teması: {setup.Tema}
            Ek Detaylar: {setup.Detay}

            KURALLAR:
            1. Kısa, sürükleyici ve atmosferik bir giriş yap.
            2. Oyuncu adına karar verme; yalnızca çevreyi, NPC'leri ve sonuçları yönet.
            3. Her cevabın sonunda oyuncuya ne yapmak istediğini sor.
            4. Kahraman koruması yoktur. Mantıksız kararların gerçekçi sonuçları olabilir; ölüm ve kalıcı kayıp mümkündür. Grafik veya dehşet verici ayrıntı verme.
            5. Hikâyeyi en fazla {MaxTurns} hamlede tamamlanacak biçimde yönet.
            6. Açık cinsel içerik, reşit olmayanların cinselleştirilmesi, kendine zarar verme içeriği veya grafik şiddet üretme.
            7. Her cevabın sonunda aşağıdaki biçimde geçerli JSON sahne verisi üret. JSON dışında etiket içine açıklama yazma:
            [SCENE_JSON]
            {{"location":"mekân adı","setting":"indoor|outdoor","time":"day|sunset|night","weather":"clear|rain|snow|fog","mood":"calm|happy|tense|mysterious|sad","action":"idle|talk|walk|run|sit|celebrate|argue|fight|give|look","player":{{"x":25,"emotion":"neutral|happy|sad|angry|surprised","outfit":"casual|formal|royal|armor|robe|uniform","hair":"black|brown|blonde|red|gray","color":"#22c55e"}},"npcs":[{{"name":"NPC","x":70,"emotion":"neutral","outfit":"casual","color":"#3b82f6"}}],"objects":["table","chair"],"caption":"Kısa sahne açıklaması"}}
            [/SCENE_JSON]
            8. x değerleri 10 ile 90 arasında olmalı. En fazla 3 NPC ve 5 nesne üret. Karakterlerin renk ve görünüşlerini önceki sahnelerle tutarlı koru.
            """;

        state.History.Add(Message("user", systemPrompt + "\n\nHikâyeyi başlat."));
        var result = await AskGemini(state.History);
        if (!result.Ok) { Games.TryRemove(gameId, out _); return StatusCode(result.StatusCode, new { error = result.Error }); }

        state.History.Add(Message("model", result.Text!));
        var parsed = ParseAnswer(result.Text!);
        return Ok(new { gameId, parsed.Response, parsed.Scene, turn = state.TurnCount, maxTurns = MaxTurns });
    }

    [HttpPost("action")]
    public async Task<IActionResult> PlayerAction([FromBody] PlayerInput input)
    {
        if (input.GameId == Guid.Empty || !Games.TryGetValue(input.GameId, out var state))
            return NotFound(new { error = "Oyun bulunamadı. Yeni oyun başlat." });
        if (string.IsNullOrWhiteSpace(input.Action)) return BadRequest(new { error = "Eylem boş olamaz." });

        await state.Lock.WaitAsync();
        try
        {
            if (state.TurnCount >= MaxTurns) return BadRequest(new { error = "Bu hikâye tamamlandı." });
            state.TurnCount++;
            state.LastAccessUtc = DateTime.UtcNow;
            var action = $"[Tur: {state.TurnCount}/{MaxTurns}] Oyuncu hamlesi: {input.Action}";
            if (state.TurnCount == MaxTurns) action += "\nBu son hamledir; hikâyeyi sonuçlara uygun biçimde tamamen sonlandır.";
            state.History.Add(Message("user", action));

            var result = await AskGemini(state.History);
            if (!result.Ok)
            {
                state.History.RemoveAt(state.History.Count - 1);
                state.TurnCount--;
                return StatusCode(result.StatusCode, new { error = result.Error });
            }

            state.History.Add(Message("model", result.Text!));
            var parsed = ParseAnswer(result.Text!);
            return Ok(new { input.GameId, parsed.Response, parsed.Scene, turn = state.TurnCount, maxTurns = MaxTurns });
        }
        finally { state.Lock.Release(); }
    }

    private async Task<ApiResult> AskGemini(List<object> history)
    {
        var key = _config["GeminiApiKey"] ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        if (string.IsNullOrWhiteSpace(key)) return new(false, null, "Gemini API anahtarı sunucuda ayarlanmamış.", 500);
        try
        {
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent?key={Uri.EscapeDataString(key.Trim())}";
            var json = JsonSerializer.Serialize(new { contents = history });
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await _httpClientFactory.CreateClient().PostAsync(url, content);
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode) return new(false, null, $"Gemini isteği başarısız: {(int)response.StatusCode}", (int)response.StatusCode);
            using var doc = JsonDocument.Parse(body);
            var text = doc.RootElement.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString();
            return string.IsNullOrWhiteSpace(text) ? new(false, null, "Gemini boş yanıt verdi.", 502) : new(true, text, null, 200);
        }
        catch (Exception ex) { return new(false, null, $"Gemini bağlantı hatası: {ex.Message}", 502); }
    }

    private static object Message(string role, string text) => new { role, parts = new[] { new { text } } };

    private static ParsedAnswer ParseAnswer(string full)
    {
        const string startTag = "[SCENE_JSON]";
        const string endTag = "[/SCENE_JSON]";
        var start = full.LastIndexOf(startTag, StringComparison.OrdinalIgnoreCase);
        var end = full.LastIndexOf(endTag, StringComparison.OrdinalIgnoreCase);
        if (start >= 0 && end > start)
        {
            var story = full[..start].Trim();
            var json = full[(start + startTag.Length)..end].Trim();
            try
            {
                using var document = JsonDocument.Parse(json);
                return new(story, document.RootElement.Clone());
            }
            catch (JsonException) { }
        }

        var fallback = JsonSerializer.SerializeToElement(new
        {
            location = "Bilinmeyen bölge", setting = "outdoor", time = "day", weather = "clear",
            mood = "mysterious", action = "idle",
            player = new { x = 30, emotion = "neutral", outfit = "casual", hair = "black", color = "#22c55e" },
            npcs = Array.Empty<object>(), objects = Array.Empty<string>(), caption = "Yeni bir sahne"
        });
        return new(full.Replace(startTag, "").Replace(endTag, "").Trim(), fallback);
    }

    private static void RemoveExpiredGames()
    {
        var cutoff = DateTime.UtcNow.AddHours(-6);
        foreach (var game in Games.Where(x => x.Value.LastAccessUtc < cutoff)) Games.TryRemove(game.Key, out _);
    }

    private sealed class GameState
    {
        public List<object> History { get; } = new();
        public int TurnCount { get; set; }
        public DateTime LastAccessUtc { get; set; } = DateTime.UtcNow;
        public SemaphoreSlim Lock { get; } = new(1, 1);
    }

    private sealed record ApiResult(bool Ok, string? Text, string? Error, int StatusCode);
    private sealed record ParsedAnswer(string Response, JsonElement Scene);
}

public sealed class GameSetup
{
    public string Dunya { get; set; } = "";
    public string Rol { get; set; } = "";
    public string Tema { get; set; } = "";
    public string Detay { get; set; } = "";
}

public sealed class PlayerInput
{
    public Guid GameId { get; set; }
    public string Action { get; set; } = "";
}
