# ---------------------------------------------------------------- build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore against the project files alone, so a source-only edit reuses the layer.
COPY src/StiflingDark.Engine/StiflingDark.Engine.csproj     src/StiflingDark.Engine/
COPY src/StiflingDark.Protocol/StiflingDark.Protocol.csproj src/StiflingDark.Protocol/
COPY src/StiflingDark.Bots/StiflingDark.Bots.csproj         src/StiflingDark.Bots/
COPY src/StiflingDark.Server/StiflingDark.Server.csproj     src/StiflingDark.Server/
RUN dotnet restore src/StiflingDark.Server/StiflingDark.Server.csproj

COPY src/ src/
RUN dotnet publish src/StiflingDark.Server/StiflingDark.Server.csproj \
    -c Release -o /app --no-restore

# -------------------------------------------------------------- runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app ./

# The rules live in data, not in code: maps, the raster line-of-sight masks, and every card
# deck. GAME_DATA_DIR points the server at them; without this the server refuses to boot.
COPY game-data/ /app/game-data/
ENV GAME_DATA_DIR=/app/game-data

# Room snapshots land here. No VOLUME instruction on purpose - Railway rejects it
# ("use Railway Volumes"); attach the platform volume (or a compose volume) at /data.
ENV DATA_DIR=/data

# Milliseconds between bot turns; 0 makes bot seats instant.
ENV BOT_DELAY_MS=1100

ENV PORT=5226
EXPOSE 5226

# No HEALTHCHECK here on purpose: the aspnet runtime image ships neither curl nor wget, so an
# in-container probe would need an extra apt layer to say what an external HTTP GET of
# /health already says. Point your platform's probe at GET /health instead.

ENTRYPOINT ["dotnet", "StiflingDark.Server.dll"]
