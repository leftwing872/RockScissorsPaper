# syntax=docker/dockerfile:1

# ---- Build stage ------------------------------------------------------------
# Compiles and publishes the server as a framework-dependent app.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore first (cached layer) - only the csproj changes invalidate this.
COPY Take1Server.csproj ./
RUN dotnet restore Take1Server.csproj

# Copy the rest of the source (dll/ is needed because the csproj references
# dll/GameLiftServerSDK.dll via a HintPath).
COPY . ./

# The csproj sets <PublishSingleFile>true</PublishSingleFile>, which requires a
# RuntimeIdentifier. For a container we publish framework-dependent instead, so
# we override it off here and run via "dotnet Take1Server.dll".
RUN dotnet publish Take1Server.csproj \
    -c Release \
    -o /app/publish \
    --no-restore \
    -p:PublishSingleFile=false

# ---- Runtime stage ----------------------------------------------------------
# Console app (no ASP.NET), so the plain runtime image is enough.
FROM mcr.microsoft.com/dotnet/runtime:8.0 AS runtime
WORKDIR /app

COPY --from=build /app/publish ./

# Game server TCP port (must match the Agones GameServer manifest containerPort).
EXPOSE 7777

# Agones sidecar gRPC endpoint defaults (overridable by the injected env in k8s).
ENV AGONES_SDK_GRPC_HOST=localhost \
    AGONES_SDK_GRPC_PORT=9357

ENTRYPOINT ["dotnet", "Take1Server.dll"]
