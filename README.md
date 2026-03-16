# LINE Agent Template

A .NET Web API template with LINE Official Account integration for building notification bots and command-driven agents.

## Features

- **LINE Webhook** — Command router pattern (`/add`, `/list`, `/done`, `/help`)
- **Reply API** — Command responses are free (no quota cost)
- **Push API** — Scheduled notifications (daily reminders, etc.)
- **Quota Check** — `/quota` command to monitor message usage
- **Dual DB** — SQLite (default, zero config) / MSSQL (optional)
- **Hangfire** — Built-in job scheduler for recurring notifications
- **Deploy Scripts** — One-command VM deployment with systemd + ngrok

## Quick Start

### 1. Create LINE Official Account

1. Go to [LINE Official Account Manager](https://manager.line.biz/) → Create account
2. Enable Messaging API in Settings
3. Go to [LINE Developers Console](https://developers.line.biz/console/)
4. Note down: **Channel ID**, **Channel Secret**, **Channel Access Token**, **Your User ID**

### 2. Configure

```bash
cp .env.example .env
# Edit .env with your LINE credentials
```

Or edit `src/LineAgent.Api/appsettings.json` directly.

### 3. Run

```bash
cd src/LineAgent.Api
dotnet run
```

API starts at `http://localhost:5010`. Test:

```bash
# Test API
curl http://localhost:5010/api/items

# Test LINE push
curl -X POST http://localhost:5010/api/notifications/test
```

### 4. Set Webhook URL

For local development, use ngrok:

```bash
ngrok http 5010
# Copy the https URL
```

Set webhook in LINE Developers Console:
```
https://your-ngrok-url.ngrok-free.dev/api/line/webhook
```

Or via API:
```bash
curl -X PUT https://api.line.me/v2/bot/channel/webhook/endpoint \
  -H "Authorization: Bearer YOUR_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"endpoint":"https://your-url/api/line/webhook"}'
```

## LINE Commands

| Command | Description |
|---------|-------------|
| `/add title` | Add item |
| `/add !1 title` | Add high priority (🔴1, 🟡2, ⚪3) |
| `/add title @category` | Add with category |
| `/list` | List pending items |
| `/done 1` | Complete item #1 |
| `/done 1,2,3` | Complete multiple |
| `/today` | Today's summary |
| `/quota` | Check message quota |
| `/help` | Show commands |

## Deploy to VM

```bash
# One-command deploy to GCE
bash deploy/publish.sh <project-id> <vm-name> <zone>

# Or manual setup on any Linux VM
export LINE_CHANNEL_SECRET=xxx
export LINE_CHANNEL_ACCESS_TOKEN=xxx
export LINE_DEFAULT_USER_ID=Uxxx
export NGROK_AUTHTOKEN=xxx  # optional
bash deploy/setup.sh
```

## Architecture

```
LINE User  ──message──▶  LINE Platform  ──webhook──▶  API (Reply, free)
LINE User  ◀──push────  LINE Platform  ◀──push─────  Hangfire (costs quota)
```

| Layer | Tech |
|-------|------|
| API | .NET 10 Web API |
| DB | SQLite (default) / MSSQL |
| ORM | Dapper |
| Scheduler | Hangfire |
| Tunnel | ngrok |

## Customization

1. **Add commands** — Edit `LineWebhookController.HandleTextMessageAsync()`
2. **Add entities** — Create in `Models/Entities/`, add to SQLite init in `Program.cs`
3. **Add scheduled jobs** — Create in `Jobs/NotificationJobs.cs`, register in `Program.cs`
4. **Add push templates** — Add methods to `LineMessagingService`

## Cost

| Item | Cost |
|------|------|
| LINE Communication Plan | Free (200 push/month) |
| GCE e2-micro | Free tier |
| ngrok | Free (random URL, changes on restart) |
| Reply messages | **Always free** |
