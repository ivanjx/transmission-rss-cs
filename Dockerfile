FROM mcr.microsoft.com/dotnet/sdk:10.0-noble-aot AS build

WORKDIR /source
COPY TransmissionRss.slnx ./
COPY src/TransmissionRss/TransmissionRss.csproj src/TransmissionRss/

RUN dotnet restore src/TransmissionRss/TransmissionRss.csproj --runtime linux-x64

COPY src/TransmissionRss/ src/TransmissionRss/
RUN dotnet publish src/TransmissionRss/TransmissionRss.csproj --configuration Release --runtime linux-x64 --no-restore --output /app


FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble AS final

RUN apt-get update \
    && apt-get install --no-install-recommends --yes curl \
    && rm -rf /var/lib/apt/lists/*

ENV ASPNETCORE_HTTP_PORTS=8354 \
    Database__Path=/data/transmission-rss.db
WORKDIR /app
VOLUME ["/data"]
EXPOSE 8354

COPY --from=build --chown=$APP_UID:$APP_UID /app/ ./
USER $APP_UID

HEALTHCHECK --interval=30s --timeout=20s --start-period=10s --retries=3 CMD ["curl", "--fail", "--silent", "--show-error", "--max-time", "15", "http://127.0.0.1:8354/health"]

ENTRYPOINT ["./TransmissionRss"]
