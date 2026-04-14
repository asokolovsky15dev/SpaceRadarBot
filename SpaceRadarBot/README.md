# Space Radar Bot

A Telegram bot for tracking upcoming space launches with notification support.

## Features

- View next 5 upcoming space launches
- **Russian translation** - Launch descriptions are automatically translated from English to Russian
- **Reusable rocket tracking** - See booster serial number, flight number, and landing attempt status
- Subscribe to launch notifications (30 minutes before liftoff)
- **Automatic notification preferences** - Set your preference once and get notified about all matching launches:
  - All launches
  - Only 5-star launches (most spectacular)
  - 4-star and above launches
- Manual subscriptions for individual launches
- Live stream links when available
- Support for multiple users
- Persistent main menu with bot commands
- Timezone support for personalized time display

## Usage

### Commands

- `/start` - Get welcome message and instructions
- `/next` - View next 5 upcoming launches with subscription buttons
- `/settings` - Configure automatic notification preferences

### Notification Options

1. **Manual Subscriptions**: Click the "🔔 Subscribe for notification" button under any upcoming launch to receive a notification 30 minutes before that specific launch.

2. **Automatic Subscriptions**: Use `/settings` to set your preference:
   - **All launches** - Get notified about every upcoming launch
   - **Only 5⭐** - Get notified only about the most spectacular launches
   - **4⭐ and above** - Get notified about highly rated launches
   - **None** - Only use manual subscriptions

Once you set a preference, you'll automatically receive notifications for all matching launches without having to subscribe manually!

## Architecture

```
SpaceRadarBot/
├── Program.cs              # Entry point and bot initialization
├── Handlers/
│   └── BotHandlers.cs      # Telegram command and callback handlers
├── Services/
│   ├── LaunchService.cs    # Fetches launch data from cache
│   ├── LaunchSyncService.cs # Syncs launches from API
│   └── NotificationService.cs # Background notification sender
├── Data/
│   └── DatabaseService.cs  # LiteDB database access
└── Models/
    ├── Launch.cs           # Launch data model
    ├── Subscription.cs     # Subscription data model
    ├── UserPreference.cs   # User notification preferences
    └── LaunchLibraryModels.cs # API response models
```

## Dependencies

- Telegram.Bot (21.14.1) - Telegram Bot API client
- LiteDB (5.0.21) - Lightweight embedded database
- Microsoft.Extensions.Configuration - Configuration management

## Deployment to VPS

### Deploy to DigitalOcean/Linux VPS

1. **Publish the application:**
   ```bash
   dotnet publish -c Release -r linux-x64 --self-contained -o ./publish
   ```

2. **Upload to VPS:**
   ```bash
   scp -r ./publish/* root@your-vps-ip:/opt/spaceradar/
   ```

3. **Set environment variable on VPS:**
   ```bash
   ssh root@your-vps-ip

   # Create systemd service file
   sudo nano /etc/systemd/system/spaceradar.service
   ```

4. **Add this configuration:**
   ```ini
   [Unit]
   Description=Space Radar Telegram Bot
   After=network.target

   [Service]
   Type=simple
   User=root
   WorkingDirectory=/opt/spaceradar
   ExecStart=/opt/spaceradar/SpaceRadarBot
   Restart=always
   RestartSec=10
   Environment="BOT_TOKEN=your_bot_token_here"
   Environment="DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1"

   [Install]
   WantedBy=multi-user.target
   ```

5. **Start the service:**
   ```bash
   chmod +x /opt/spaceradar/SpaceRadarBot
   sudo systemctl enable spaceradar
   sudo systemctl start spaceradar
   sudo systemctl status spaceradar

   # View logs
   sudo journalctl -u spaceradar -f
   ```

## Security

⚠️ **Never commit your bot token to version control!**

- `appsettings.json` is excluded from Git via `.gitignore`
- Always use environment variables in production
- The example config (`appsettings.example.json`) is safe to commit

## Data Source

- Telegram.Bot (21.14.1) - Telegram Bot API client
- LiteDB (5.0.21) - Lightweight embedded database

## Data Source

Launch data is fetched from [The Space Devs Launch Library API](https://thespacedevs.com/), a free and open-source API for space launch information.
