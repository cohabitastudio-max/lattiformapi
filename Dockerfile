# ============================================================
#  LATTIFORM API — Dockerfile multi-stage
#  .NET 9 + PicoGK 1.7.7.5 headless (compilado desde source)
#  Base: Ubuntu 22.04 (Jammy)
# ============================================================

# ── Stage 1: Compilar picogk.so desde source ─────────────
FROM ubuntu:22.04 AS picogk-builder

ENV DEBIAN_FRONTEND=noninteractive

RUN apt-get update && apt-get install -y \
    build-essential \
    cmake \
    git \
    ninja-build \
    pkg-config \
    libtbb-dev \
    libboost-all-dev \
    libblosc-dev \
    zlib1g-dev \
    liblz4-dev \
    libzstd-dev \
    && rm -rf /var/lib/apt/lists/*

# Clonar PicoGKRuntime con submodules
RUN git clone --recurse-submodules --depth=1 \
    https://github.com/leap71/PicoGKRuntime.git /src/PicoGKRuntime

WORKDIR /src/PicoGKRuntime

# Compilar en modo headless (sin GLFW/OpenGL)
RUN cmake -B build -G Ninja \
    -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_POSITION_INDEPENDENT_CODE=ON \
    -DPICOGK_BUILD_VIEWER=OFF \
    && cmake --build build --config Release -j$(nproc)

# El .so estará en build/Dist/ o build/
RUN find /src/PicoGKRuntime/build -name "*.so" | head -5

# ── Stage 2: Build .NET app ───────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build

WORKDIR /src
COPY LattiformAPI.csproj .
RUN dotnet restore --runtime linux-x64

COPY . .
RUN dotnet publish -c Release -r linux-x64 --self-contained false -o /app/publish

# ── Stage 3: Runtime final ────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime

RUN apt-get update && apt-get install -y \
    libgomp1 \
    libtbb12 \
    libstdc++6 \
    libblosc1 \
    zlib1g \
    liblz4-1 \
    libzstd1 \
    wget \
    && rm -rf /var/lib/apt/lists/*

# Copiar picogk.so compilado desde el builder
COPY --from=picogk-builder /src/PicoGKRuntime/build/Dist/picogk.so /usr/local/lib/picogk.so
RUN ldconfig

WORKDIR /app
COPY --from=build /app/publish .

# Copiar .so también junto al ejecutable (fallback de carga nativa)
RUN cp /usr/local/lib/picogk.so . 2>/dev/null || true

ENV ASPNETCORE_URLS="http://+:${PORT:-8080}"
ENV ASPNETCORE_ENVIRONMENT="Production"

HEALTHCHECK --interval=30s --timeout=10s --start-period=90s --retries=3 \
    CMD wget -qO- http://localhost:${PORT:-8080}/health || exit 1

EXPOSE 8080
ENTRYPOINT ["dotnet", "LattiformAPI.dll"]
