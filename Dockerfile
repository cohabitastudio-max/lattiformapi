# ============================================================
#  LATTIFORM API — Dockerfile multi-stage
#  .NET 9 + PicoGK headless (compilado desde source)
#  Base: Ubuntu 22.04 + CMake 3.28 desde Kitware
#
#  PicoGKGLViewer.cpp incluye <GLFW/glfw3.h> incondicionalmente.
#  Solución: compilar GLFW primero como librería estática,
#  luego compilar PicoGK apuntando a sus includes.
#  El .so final no requiere display server en runtime.
# ============================================================

# ── Stage 1: Compilar picogk.so desde source ─────────────
FROM ubuntu:22.04 AS picogk-builder

ENV DEBIAN_FRONTEND=noninteractive

# Todas las dependencias incluyendo display headers para GLFW
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
    libwayland-dev \
    wayland-protocols \
    libxkbcommon-dev \
    libx11-dev \
    libxrandr-dev \
    libxinerama-dev \
    libxcursor-dev \
    libxi-dev \
    libgl-dev \
    libgles2-mesa-dev \
    && rm -rf /var/lib/apt/lists/*

# CMake >= 3.25 desde Kitware
RUN wget -qO /tmp/kitware.sh https://apt.kitware.com/kitware-archive.sh \
    && bash /tmp/kitware.sh \
    && apt-get update && apt-get install -y cmake \
    && cmake --version \
    && rm -rf /var/lib/apt/lists/*

# Clonar PicoGKRuntime con submodules (GLFW + openvdb)
RUN git clone --recurse-submodules --depth=1 \
    https://github.com/leap71/PicoGKRuntime.git /src/PicoGKRuntime

WORKDIR /src/PicoGKRuntime

# Patch CMakeLists: quitar glfw del link final
# (GLFW se compila como submodule pero el .so headless no lo necesita en runtime)
RUN sed -i \
    's/target_link_libraries(${LIB_NAME} openvdb_static glfw )/target_link_libraries(${LIB_NAME} openvdb_static)/' \
    CMakeLists.txt \
    && echo "✅ patch glfw link:" \
    && grep "target_link_libraries.*LIB_NAME" CMakeLists.txt

# Compilar: GLFW se incluirá en el árbol CMake y proveerá los headers
# Los binarios de GLFW se compilan pero no se linkean en el .so final
RUN cmake -B build -G Ninja \
    -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_POSITION_INDEPENDENT_CODE=ON \
    -DGLFW_BUILD_DOCS=OFF \
    -DGLFW_BUILD_TESTS=OFF \
    -DGLFW_BUILD_EXAMPLES=OFF \
    && cmake --build build --config Release -j$(nproc)

# Ubicar el .so generado
RUN echo "=== .so files ===" \
    && find /src/PicoGKRuntime -name "picogk*.so" \
    && echo "=== Dist ===" \
    && ls /src/PicoGKRuntime/Dist/ 2>/dev/null || true \
    && echo "=== build/lib ===" \
    && ls /src/PicoGKRuntime/build/lib/ 2>/dev/null || true

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

# Copiar picogk.so — buscar en Dist/ y lib/
COPY --from=picogk-builder /src/PicoGKRuntime /tmp/picogk-src
RUN SO=$(find /tmp/picogk-src -name "picogk.so" | head -1) \
    && echo "Copiando: $SO" \
    && cp "$SO" /usr/local/lib/picogk.so \
    && ldconfig \
    && ls -lh /usr/local/lib/picogk.so \
    && rm -rf /tmp/picogk-src

WORKDIR /app
COPY --from=build /app/publish .

RUN cp /usr/local/lib/picogk.so . 2>/dev/null || true

ENV ASPNETCORE_URLS="http://+:${PORT:-8080}"
ENV ASPNETCORE_ENVIRONMENT="Production"

HEALTHCHECK --interval=30s --timeout=10s --start-period=90s --retries=3 \
    CMD wget -qO- http://localhost:${PORT:-8080}/health || exit 1

EXPOSE 8080
ENTRYPOINT ["dotnet", "LattiformAPI.dll"]
