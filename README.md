# AITextGame

Tek ASP.NET Core uygulaması içinde arayüz, Gemini hikâye API'si ve ücretsiz prosedürel 2D sahne sistemi bulunur.

## Yerel çalıştırma

API anahtarlarını kaynak koduna yazmayın. Visual Studio kullanıcı gizlileriyle ekleyin:

```powershell
dotnet user-secrets set "GeminiApiKey" "GEMINI_ANAHTARINIZ"
dotnet run
```

Yayın sunucusunda şu ortam değişkenlerini ekleyin:

```text
GEMINI_API_KEY
```

`appsettings.json` içine gerçek anahtar eklemeyin ve anahtarları GitHub'a göndermeyin.

## GitHub Pages kullanımı

En kolay yöntem, arayüzü de backend ile aynı sunucudan açmaktır. GitHub Pages kullanılacaksa `wwwroot/index.html` içindeki `API_BASE_URL` değerine yayınlanmış backend adresini yazın.
