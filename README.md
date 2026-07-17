# Transmission RSS

A small, native-AOT ASP.NET Core service that watches RSS/Atom feeds and adds matching torrents to Transmission.

## Run with Docker

```sh
docker run --name transmission-rss -p 8354:8354 \
  -v transmission-rss-data:/data \
  ghcr.io/ivanjx/transmission-rss-cs:latest
```

Open `http://localhost:8354`, enter the Transmission RPC URL, add a feed, then add one or more rules to it. Put the service behind an authenticating reverse proxy; the app intentionally has no built-in authentication.

Alternatively, use the included Compose file:

```sh
docker compose up -d
```

## Development

Requires the .NET 10 SDK.

```sh
dotnet test TransmissionRss.slnx
dotnet run --project src/TransmissionRss
```

A native executable can be produced with:

```sh
dotnet publish src/TransmissionRss -c Release -r linux-x64
```

## License

MIT
