# ============================================================
#  LATTIFORM API — Dockerfile multi-stage
#  .NET 9 + PicoGK 1.7.7.5 headless (compilado desde source)
#  Base: Ubuntu 22.04 (Jammy) + CMake 3.28 desde Kitware
#
#  GLFW está hardcodeado en PicoGKRuntime/CMakeLists.txt —
#  lo parcheamos para eliminar la dependencia de display server.
#  GLFW se compila pero no necesita Wayland/X11 headers en headless
#  si le damos sus propias deps.
# ============================================================

# ── Stage 1: Compilar picogk.so desde source ─────────────
FROM ubuntu:22.04 AS picogk-builder

ENV DEBIAN_FRONTEND=noninteractive

# Dependencias de compilación incluyendo Wayland/X11 para GLFW headless
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
    # GLFW necesita estas aunque compilemos headless
    libwayland-dev \
    wayland-protocols \
    libxkbcommon-dev \
    libx11-dev \
    libxrandr-dev \
    libxinerama-dev \
    libxcursor-dev \
    libxi-dev \
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

# Patch: eliminar GLFW del link y del add_subdirectory
# GLFW aún se declara pero no se linkea — así evitamos el viewer
# sin romper el árbol de CMake
RUN sed -i 's/target_link_libraries(${LIB_NAME} openvdb_static glfw )/target_link_libraries(${LIB_NAME} openvdb_static)/' CMakeLists.txt \
    && grep "target_link_libraries" CMakeLists.txt

# Compilar en modo Release sin viewer
RUN cmake -B build -G Ninja \
    -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_POSITION_INDEPENDENT_CODE=ON \
    -DGLFW_BUILD_DOCS=OFF \
    -DGLFW_BUILD_TESTS=OFF \
    -DGLFW_BUILD_EXAMPLES=OFF \
    && cmake --build build --config Release -j$(nproc)

# Mostrar dónde quedó el .so
RUN echo "=== .so generados ===" \
    && find /src/PicoGKRuntime -name "*.so" \
    && echo "=== Dist ===" \
    && ls /src/PicoGKRuntime/Dist/ 2>/dev/null || true

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

# Copiar picogk.so — buscar en Dist/ o en lib/
COPY --from=picogk-builder /src/PicoGKRuntime /tmp/picogk-src
RUN find /tmp/picogk-src -name "picogk.so" | head -3 \
    && find /tmp/picogk-src -name "picogk.so" -exec cp {} /usr/local/lib/picogk.so \; \
    && ldconfig \
    && ls -lh /usr/local/lib/picogk.so \
    && rm -rf /tmp/picogk-src

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
