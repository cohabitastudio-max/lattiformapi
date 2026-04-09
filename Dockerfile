# ============================================================
#  LATTIFORM API — Dockerfile multi-stage
#  .NET 9 + PicoGK headless (compilado desde source)
#  Base: Ubuntu 22.04 + CMake 3.28 desde Kitware
# ============================================================

# ── Stage 1: Compilar picogk.so desde source ─────────────
FROM ubuntu:22.04 AS picogk-builder

ENV DEBIAN_FRONTEND=noninteractive

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

# Patch 1: agregar GLFW/include al include path del target picogk
# Patch 2: quitar glfw del link (no necesitamos display en runtime)
RUN python3 - << 'PYEOF'
with open('CMakeLists.txt', 'r') as f:
    txt = f.read()

# Patch 1: agregar include path de GLFW headers justo antes de target_link_libraries
glfw_include = (
    "# Headless: exponer headers de GLFW sin linkear la lib\n"
    "target_include_directories(${LIB_NAME} PRIVATE ${PICOGK_ROOT_DIR}/GLFW/include)\n"
)
insert_point = "target_link_libraries(${LIB_NAME} openvdb_static glfw )"
txt = txt.replace(insert_point, glfw_include + insert_point)

# Patch 2: quitar glfw del link
txt = txt.replace(
    "target_link_libraries(${LIB_NAME} openvdb_static glfw )",
    "target_link_libraries(${LIB_NAME} openvdb_static)"
)

with open('CMakeLists.txt', 'w') as f:
    f.write(txt)

print("✅ Patches aplicados")
PYEOF

# Verificar patches
RUN echo "=== target_include_directories ===" \
    && grep -n "GLFW/include\|target_include_directories.*LIB_NAME\|target_link_libraries.*LIB_NAME" CMakeLists.txt

# Compilar
RUN cmake -B build -G Ninja \
    -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_POSITION_INDEPENDENT_CODE=ON \
    -DGLFW_BUILD_DOCS=OFF \
    -DGLFW_BUILD_TESTS=OFF \
    -DGLFW_BUILD_EXAMPLES=OFF \
    && cmake --build build --config Release -j$(nproc)

# Ubicar el .so
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

# Copiar picogk.so
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
