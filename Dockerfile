FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY *.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:9.0
RUN apt-get update && apt-get install -y --no-install-recommends \
    libtbb12 libgomp1 wget && rm -rf /var/lib/apt/lists/*

# Try to get PicoGK native - won't fail if unavailable
RUN wget -q "https://github.com/leap71/PicoGKRuntime/releases/latest/download/libpicogk.so" \
    -O /usr/local/lib/libpicogk.so 2>/dev/null \
    || echo "PicoGK native not available - using MathSTL fallback"
RUN ldconfig 2>/dev/null || true

WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:10000
ENV DOTNET_EnableDiagnostics=0
EXPOSE 10000
ENTRYPOINT ["dotnet", "LattiformAPI.dll"]
