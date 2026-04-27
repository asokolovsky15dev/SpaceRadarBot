using System.Text;
using System.Text.Json;

namespace SpaceRadarBot.Services;

public class TranslationService
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly string _model;
    private const string OpenAiApiUrl = "https://api.openai.com/v1/chat/completions";

    public TranslationService(string apiKey, string model = "gpt-3.5-turbo")
    {
        _apiKey = apiKey;
        _model = model;
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
    }

    public async Task<string?> TranslateToRussianAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        try
        {
            var requestBody = new
            {
                model = _model,
                messages = new[]
                {
                    new
                    {
                        role = "system",
                        content = "You are a professional translator. Translate the following space launch description from English to Russian. Maintain technical accuracy and keep proper nouns in their original form."
                    },
                    new
                    {
                        role = "user",
                        content = text
                    }
                },
                temperature = 0.3,
                max_tokens = 500
            };

            var json = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(OpenAiApiUrl, content);
            var responseContent = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"❌ OpenAI API error: {response.StatusCode} - {responseContent}");
                return null;
            }

            var result = JsonSerializer.Deserialize<OpenAiResponse>(responseContent, new JsonSerializerOptions 
            { 
                PropertyNameCaseInsensitive = true 
            });
            var translation = result?.Choices?.FirstOrDefault()?.Message?.Content?.Trim();

            return translation;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Translation error: {ex.Message}");
            return null;
        }
    }

    public async Task<Dictionary<string, string>> TranslateBatchAsync(Dictionary<string, string> texts)
    {
        var results = new Dictionary<string, string>();

        foreach (var (key, text) in texts)
        {
            var translation = await TranslateToRussianAsync(text);
            if (translation != null)
            {
                results[key] = translation;
            }
            await Task.Delay(100); // Rate limiting
        }

        return results;
    }

    private class OpenAiResponse
    {
        public List<Choice>? Choices { get; set; }
    }

    private class Choice
    {
        public Message? Message { get; set; }
    }

    private class Message
    {
        public string? Content { get; set; }
    }
}
