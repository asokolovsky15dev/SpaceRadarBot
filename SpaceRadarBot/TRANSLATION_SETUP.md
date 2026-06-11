# OpenAI Translation Setup Guide

## Overview

The bot now automatically translates launch descriptions from English to Russian using **OpenAI API** (GPT-3.5-turbo or GPT-4) - providing high-quality AI-powered translations.

## ✨ Key Features

- **Smart Caching**: Translations are stored in the database
- **Intelligent**: Only translates when the English description changes
- **Automatic**: Runs in the background during sync (every 10 minutes)
- **Graceful Degradation**: Bot works fine without translation if API key is not provided
- **Rate Limiting**: Built-in delays to respect API quotas

## 🚀 Quick Setup

### 1. Get OpenAI API Key

1. Go to https://platform.openai.com/
2. Sign up or log in to your account
3. Navigate to **API Keys** section
4. Click **Create new secret key**
5. Copy your API key (starts with `sk-...`)
6. Add payment method (required for API usage)

### 2. Add to Configuration

**Option A: Local Development** (`appsettings.json`):
```json
{
  "BotToken": "your_telegram_bot_token",
  "OpenAI": {
    "ApiKey": "sk-your-actual-api-key-here",
    "Model": "gpt-3.5-turbo"
  },
  "Database": {
    "Path": "spaceradar.db"
  }
}
```

**Option B: Environment Variable** (Production):
```bash
export OPENAI_API_KEY="sk-your-api-key-here"
```

### 3. Choose Your Model

Available models:
- **`gpt-3.5-turbo`** ✅ Recommended (cheap, fast, good quality)
- **`gpt-4`** - Best quality but expensive (~20x cost)
- **`gpt-4-turbo`** - Good balance of quality and cost

### 4. That's It!

The bot will automatically:
- ✅ Translate new launch descriptions
- ✅ Skip already-translated launches
- ✅ Re-translate only when English description changes
- ✅ Show console logs: `✅ Translated: Falcon 9 Block 5 | Starlink`

## 📊 How It Works

1. **Launch Sync** (every 10 minutes):
   - Fetches upcoming launches from API
   - Saves to database with original English descriptions

2. **Translation Check**:
   - Finds launches with English description but no Russian translation
   - **Skips** launches that already have Russian translation in DB

3. **Smart Translation**:
   ```
   IF launch has description AND no Russian translation:
       ✅ Translate to Russian (uses API tokens)
   ELSE IF Russian translation exists:
       ✅ Keep existing Russian translation (saves tokens!)
   ```

4. **Database Update**:
   - Saves Russian translation in `DescriptionRu` field
   - Original English stays in `Description` field

## 🔍 Monitoring

Watch the console for translation activity:

```
🌍 Translation service enabled (Model: gpt-3.5-turbo)
🌍 Starting translation of 20 launch descriptions...
✅ Translated: Falcon 9 Block 5 | Starlink Group 12-3
✅ Translated: Soyuz 2.1a | Progress MS-29
🌍 Successfully translated 15 descriptions to Russian
```

If translation is disabled:
```
⚠️ Translation service disabled (no OpenAI API key configured)
```

## 💰 Cost Estimation

**GPT-3.5-turbo pricing (as of 2024):**
- Input: $0.0005 per 1K tokens (~750 words)
- Output: $0.0015 per 1K tokens (~750 words)

**Example**: 20 upcoming launches with 200-word descriptions each
- **Input tokens**: ~300 tokens per launch = 6,000 total = **$0.003**
- **Output tokens**: ~300 tokens per launch = 6,000 total = **$0.009**
- **Total cost**: ~$0.012 (1.2 cents) for 20 launches

**Monthly estimate**: 
- Initial sync: 20 launches × $0.0006 = $0.012
- Daily updates: ~2-3 new launches × $0.0006 = $0.002
- **Monthly total**: ~$0.10 - $0.50 (under $1!)

## 🛠️ Troubleshooting

### Translation not working?

1. **Check API Key**:
   ```json
   // appsettings.json
   "OpenAI": {
     "ApiKey": "sk-..."  // Must start with sk-
   }
   ```

2. **Check Logs**:
   ```
   🌍 Translation service enabled (Model: gpt-3.5-turbo)  ← Good!
   ⚠️ Translation service disabled                        ← Check API key
   ❌ OpenAI API error: 401                               ← Invalid key
   ```

3. **Verify Database**:
   - Open `spaceradar.db` with LiteDB viewer
   - Check `launches` collection
   - Look for `DescriptionRu` field

### API Rate Limits?

If you hit rate limits:
- Increase delay in `LaunchSyncService.cs`:
  ```csharp
  await Task.Delay(500); // Change from 200ms to 500ms
  ```

### Too Expensive?

**Free alternatives**:
1. **Google Gemini API** - Free tier: 1500 requests/day
2. **Ollama** (local) - Completely free, runs on your machine

## 🌍 Production Deployment

**SystemD Service** (Linux VPS):

```ini
[Service]
Environment="BOT_TOKEN=your_telegram_token"
Environment="OPENAI_API_KEY=sk-your-api-key"
```

**Docker**:
```bash
docker run -e BOT_TOKEN=xxx -e OPENAI_API_KEY=sk-xxx spaceradarbot
```

## 📝 Technical Details

- **Service**: `TranslationService.cs`
- **Model Field**: `Launch.DescriptionRu` (nullable string)
- **Database Method**: Preserved in `UpsertLaunches()`
- **API**: OpenAI Chat Completions API
- **Default Model**: `gpt-3.5-turbo`
- **Language Pair**: `en → ru`
- **Rate Limiting**: 200ms delay between requests

## 🔄 Alternative: Google Gemini (FREE)

If you want a free option, you can switch to Google Gemini:

1. Get free API key: https://ai.google.dev/
2. Install package: `dotnet add package GenerativeAI.Google`
3. Update `TranslationService.cs` to use Gemini API
4. Free tier: 15 requests/minute, 1500/day

## 📚 Resources

- OpenAI Platform: https://platform.openai.com/
- Pricing: https://openai.com/api/pricing/
- API Documentation: https://platform.openai.com/docs/api-reference

## ❓ FAQ

**Q: Can I translate to other languages?**
A: Yes! Modify `TranslationService.cs` line 37: `LanguageCode.Russian` → your language

**Q: What if I don't set up DeepL?**
A: Bot works fine - descriptions stay in English

**Q: Can I use other translation services?**
A: Yes! Modify `TranslationService.cs` to use Azure Translator, LibreTranslate, etc.

**Q: How do I reset all translations?**
A: Delete `spaceradar.db` and restart bot (or manually clear `DescriptionRu` fields)

## 🎉 Benefits

✅ **Free** - 500k chars/month covers most use cases
✅ **High Quality** - DeepL is best-in-class for translation
✅ **Efficient** - Smart caching minimizes API calls
✅ **Optional** - Bot works without it
✅ **Simple** - Just add API key, everything else is automatic
