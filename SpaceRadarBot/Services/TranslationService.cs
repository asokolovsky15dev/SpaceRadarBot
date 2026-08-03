using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpaceRadarBot.Services;

public class TranslationService
{
    private readonly HttpClient _httpClient;
    private readonly string _model;
    private const string OpenAiApiUrl = "https://api.openai.com/v1/chat/completions";

    // С запасом для длинных описаний: обрезанный перевод кэшировался бы в БД навсегда.
    private const int MaxCompletionTokens = 1000;

    public TranslationService(string apiKey, string model = "gpt-4o-mini")
    {
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
                max_tokens = MaxCompletionTokens
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
            var choice = result?.Choices?.FirstOrDefault();

            // Обрезанный перевод не возвращаем: он закэшируется в DescriptionRu навсегда
            // и никогда не будет перепереведён. Лучше показать оригинал и попробовать
            // ещё раз на следующем синке.
            if (choice?.FinishReason == "length")
            {
                Console.WriteLine($"⚠️ Translation truncated at {MaxCompletionTokens} tokens, discarding (text length: {text.Length})");
                return null;
            }

            return choice?.Message?.Content?.Trim();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Translation error: {ex.Message}");
            return null;
        }
    }

    private class OpenAiResponse
    {
        public List<Choice>? Choices { get; set; }
    }

    private class Choice
    {
        public Message? Message { get; set; }

        [JsonPropertyName("finish_reason")]
        public string? FinishReason { get; set; }
    }

    private class Message
    {
        public string? Content { get; set; }
    }
}
