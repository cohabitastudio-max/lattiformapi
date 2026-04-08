# ============================================================
#  LATTIFORM API — Dockerfile para Render.com / Cloud Run
#  .NET 8 + PicoGK headless (sin X11, sin GPU)
# ============================================================

# ── Stage 1: Build ────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:8.0-jammy AS build

WORKDIR /src
COPY LattiformAPI.csproj .
RUN dotnet restore --runtime linux-x64

COPY . .
RUN dotnet publish -c Release -r linux-x64 --self-contained false -o /app/publish

# ── Stage 2: Runtime ──────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:8.0-jammy AS runtime

# Dependencias nativas de PicoGK (sin X11/OpenGL)
RUN apt-get update && apt-get install -y \
    libgomp1 \
    libtbb-dev \
    libstdc++6 \
    wget \
    && rm -rf /var/lib/apt/lists/*

# Descargar runtime nativo de PicoGK para Linux x64
# Fuente oficial: https://github.com/leap71/PicoGKRuntime/releases
RUN wget -q "https://github.com/leap71/PicoGKRuntime/releases/download/v1.7.7/picogk.so" \
    -O /usr/local/lib/picogk.so || \
    wget -q "https://github.com/leap71/PicoGKRuntime/releases/latest/download/picogk.so" \
    -O /usr/local/lib/picogk.so || \
    echo "WARNING: PicoGK native lib not downloaded — check GitHub releases URL"

RUN ldconfig

WORKDIR /app
COPY --from=build /app/publish .

# Copiar lib nativa junto al ejecutable (ruta alternativa)
RUN cp /usr/local/lib/picogk.so . 2>/dev/null || true

# Render.com usa la variable PORT
ENV ASPNETCORE_URLS="http://+:${PORT:-8080}"
ENV ASPNETCORE_ENVIRONMENT="Production"

HEALTHCHECK --interval=30s --timeout=10s --start-period=45s --retries=3 \
    CMD wget -qO- http://localhost:${PORT:-8080}/health || exit 1

EXPOSE 8080
ENTRYPOINT ["dotnet", "LattiformAPI.dll"]
