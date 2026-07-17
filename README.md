# Transmission RSS

A small, native-AOT ASP.NET Core service that watches RSS/Atom feeds and adds matching torrents to Transmission. Feeds, their rules, and server settings are edited in a RazorSlices web UI and stored in SQLite, so changes do not require a restart.

## Run with Docker

```sh
docker run --name transmission-rss -p 8080:8080 \
  -v transmission-rss-data:/data \
  ghcr.io/ivanjx/transmission-rss-cs:latest
```

Open `http://localhost:8080`, enter the Transmission RPC URL, add a feed, then add one or more rules to it. Put the service behind an authenticating reverse proxy; the app intentionally has no built-in authentication.

Alternatively, use the included Compose file:

```sh
docker compose up -d
```

Each feed owns its URL, deduplication mode, custom link field (such as `nyaa:infoHash`), and any number of rules. A rule has case-insensitive include and exclude term lists, a download directory, and an optional paused-add override. Every include term must appear in the title; a title is skipped when any exclude term appears. The latest 1,000 successful downloads are recorded in SQLite, can be managed from the UI, and will not be added twice unless removed from the history.

## Local development

Requires the .NET 10 SDK.

```sh
dotnet test TransmissionRss.slnx
dotnet run --project src/TransmissionRss
```

The default database is `data/transmission-rss.db`. Override it with `Database__Path`. A native executable can be produced with:

```sh
dotnet publish src/TransmissionRss -c Release -r linux-x64
```

## Container publishing

Pushes to `master` that change application, test, Docker, or workflow files run the integration tests and publish `latest` and commit-SHA tags to GHCR.

## License

MIT
