# ============================================================
#  LATTIFORM API — Dockerfile multi-stage
#  .NET 9 + PicoGK 1.7.7.5 headless (compilado desde source)
#  Base: Ubuntu 22.04 (Jammy) + CMake 3.28 desde Kitware
# ============================================================

# ── Stage 1: Compilar picogk.so desde source ─────────────
FROM ubuntu:22.04 AS picogk-builder

ENV DEBIAN_FRONTEND=noninteractive

# Dependencias base + Kitware GPG para CMake moderno
RUN apt-get update && apt-get install -y \
    build-essential \
    git \
    ninja-build \
    pkg-config \
    libtbb-dev \
    libboost-all-dev \
    libblosc-dev \
    zlib1g-dev \
    liblz4-dev \
    libzstd-dev \
    ca-certificates \
    gpg \
    wget \
    && rm -rf /var/lib/apt/lists/*

# Instalar CMake >= 3.25 desde el repo oficial de Kitware
RUN wget -qO /tmp/kitware.sh https://apt.kitware.com/kitware-archive.sh \
    && bash /tmp/kitware.sh \
    && apt-get update && apt-get install -y cmake \
    && cmake --version \
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

# Mostrar dónde quedó el .so
RUN find /src/PicoGKRuntime/build -name "*.so" | head -10

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

# Copiar picogk.so compilado — buscar en múltiples rutas posibles
COPY --from=picogk-builder /src/PicoGKRuntime/build /tmp/picogk-build
RUN find /tmp/picogk-build -name "*.so" -exec cp {} /usr/local/lib/picogk.so \; \
    && ldconfig \
    && rm -rf /tmp/picogk-build

WORKDIR /app
COPY --from=build /app/publish .

# Copia local junto al ejecutable (fallback de P/Invoke)
RUN cp /usr/local/lib/picogk.so . 2>/dev/null || true

ENV ASPNETCORE_URLS="http://+:${PORT:-8080}"
ENV ASPNETCORE_ENVIRONMENT="Production"

HEALTHCHECK --interval=30s --timeout=10s --start-period=90s --retries=3 \
    CMD wget -qO- http://localhost:${PORT:-8080}/health || exit 1

EXPOSE 8080
ENTRYPOINT ["dotnet", "LattiformAPI.dll"]
