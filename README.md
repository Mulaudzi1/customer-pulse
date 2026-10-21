# customer-pulse

A Net Promoter Score (NPS) and customer-feedback service for small businesses. Send each customer a personal survey link, or put a QR code at the till. Every comment is scored for sentiment and tagged by topic. Unhappy customers are flagged for a call-back while there's still time to fix things.

## What it does

- **Surveys**: the standard 0–10 "How likely are you to recommend us?" question plus an optional comment. Each survey can have its own question.
- **Personal links**: upload customers as CSV (`name,contact,segment`). Each one gets an unguessable link, which allows exactly one answer. Duplicate contacts are skipped.
- **Anonymous answers** through `POST /api/surveys/{id}/responses`, for QR codes or a website widget.
- **Text insights**:
  - A lexicon-based sentiment score from -1 to 1. It handles negation ("not good", "never late") within a clause, plus intensifiers, and includes some South African words ("lekker", "kak").
  - Keyword topic tags: delivery, price, staff, wait time, quality, app & website, billing.
  - It runs offline and the word lists are plain C# dictionaries you can tune.
- **Follow-ups**: a detractor (0–6) or a negative comment opens a follow-up and sends an alert to the log and, optionally, Slack. Staff close it with a note of what was done.
- **Reporting**:
  - NPS with a 95% margin of error, so 12 answers aren't read as a trend.
  - Promoter, passive and detractor counts, response rate, NPS by segment and by week (Monday-start, SA time).
  - Topics ranked by mentions, with average score and sentiment.
  - CSV export, with spreadsheet formulas neutralised.
- **Dashboard** at `/` for creating surveys and links, KPIs, the weekly trend, topics and open follow-ups.

## Architecture

```
Customer ─GET/POST /r/{token}─┐      Staff dashboard / API
QR/widget ─POST …/responses───┤              │
                              ▼              ▼
                     SurveyService ──► Reports (NPS, MoE, topics, weekly, segments)
                       │   └─ TextAnalyzer (sentiment + topics)
                       ├─ FollowUp + IAlertSink (log, Slack webhook)
                       ▼
                 SQLite via EF Core (surveys, invites, responses, follow-ups)
```

- `src/CustomerPulse`: ASP.NET Core 8 minimal API. The survey page is rendered on the server with HTML encoding, and the dashboard lives in `wwwroot`.
- `tests/CustomerPulse.Tests`: xUnit unit tests (NPS maths, sentiment, topics, CSV) and API tests through `WebApplicationFactory`, using a fixed clock and a recording alert sink.

## Run it

```bash
dotnet test
dotnet run --project src/CustomerPulse    # http://localhost:8080 (same as Docker, matches Pulse:BaseUrl)
```

```bash
docker compose up --build                 # http://localhost:8080
```

Quick try with curl:

```bash
curl -X POST localhost:8080/api/surveys -H 'Content-Type: application/json' -d '{"name":"October deliveries"}'
curl -X POST localhost:8080/api/surveys/1/invites -H 'Content-Type: text/csv' --data-binary $'Thandi,thandi@example.co.za,Gold'
# open the returned link, answer, then:
curl localhost:8080/api/surveys/1/summary
```

Sending the links is left to whatever you already use (email, SMS or WhatsApp). The invite endpoint returns them ready to paste or mail-merge.

## Configuration

| Key | Default | Meaning |
|---|---|---|
| `ConnectionStrings:Pulse` | `Data Source=pulse.db` | SQLite database |
| `Pulse:BaseUrl` | `http://localhost:8080` | public URL used in survey links |
| `Pulse:SlackWebhookUrl` | (none) | optional Slack incoming webhook for follow-up alerts; set it with an environment variable |

## API

| Method | Path | Purpose |
|---|---|---|
| POST | `/api/surveys` | create `{ name, question? }` |
| GET | `/api/surveys` | list |
| POST | `/api/surveys/{id}/close` | stop collecting |
| POST | `/api/surveys/{id}/invites` | JSON `[{name, contact, segment}]` or `text/csv`; returns links |
| GET/POST | `/r/{token}` | customer survey page |
| POST | `/api/surveys/{id}/responses` | anonymous `{ score, comment?, segment? }` |
| GET | `/api/surveys/{id}/summary?from=&to=` | KPIs, topics, weekly trend, segments |
| GET | `/api/surveys/{id}/responses?sentiment=&topic=` | browse comments |
| GET | `/api/surveys/{id}/responses.csv` | export |
| GET | `/api/followups?status=Open` | call-back list |
| POST | `/api/followups/{id}/close` | `{ note, closedBy }` |

The staff API has no login. Run it behind your reverse proxy's authentication, and expose only `/r/*` publicly.

## Licence

MIT. See [LICENSE](LICENSE).
