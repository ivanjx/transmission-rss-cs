# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble-aot AS build
WORKDIR /source
COPY TransmissionRss.slnx ./
COPY src/TransmissionRss/TransmissionRss.csproj src/TransmissionRss/
RUN dotnet restore src/TransmissionRss/TransmissionRss.csproj --runtime linux-x64
COPY src/TransmissionRss/ src/TransmissionRss/
RUN dotnet publish src/TransmissionRss/TransmissionRss.csproj --configuration Release --runtime linux-x64 --no-restore --output /app

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled AS final
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080 \
    Database__Path=/data/transmission-rss.db
VOLUME ["/data"]
EXPOSE 8080
COPY --from=build --chown=$APP_UID:$APP_UID /app/ ./
USER $APP_UID
ENTRYPOINT ["./TransmissionRss"]
