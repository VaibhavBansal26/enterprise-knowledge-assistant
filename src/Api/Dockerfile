FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props .editorconfig EnterpriseKnowledgeAssistant.sln ./
COPY src/Domain/Domain.csproj src/Domain/
COPY src/Application/Application.csproj src/Application/
COPY src/Infrastructure/Infrastructure.csproj src/Infrastructure/
COPY src/Api/Api.csproj src/Api/
COPY tests/Api.Tests/Api.Tests.csproj tests/Api.Tests/
COPY tests/Architecture.Tests/Architecture.Tests.csproj tests/Architecture.Tests/
RUN dotnet restore
COPY src/ src/
RUN dotnet publish src/Api/Api.csproj -c Release -o /app --no-restore

# Self-contained EF migrations bundle: applies pending migrations and exits, so a deploy can migrate as its own
# step (and fail before any new code serves traffic) instead of relying on MigrateOnStartup.
# Build with:  docker build -f src/Api/Dockerfile --target migrator -t kb-migrator .
# Run with:    docker run --rm kb-migrator --connection "Host=...;Database=kb;Username=...;Password=..."
FROM build AS migrator-build
RUN dotnet tool install --tool-path /tools dotnet-ef --version 9.*
# The bundle is self-contained, so its runtime identifier must match the image's CPU (amd64 or arm64).
ARG TARGETARCH
RUN rid=$([ "$TARGETARCH" = "arm64" ] && echo linux-arm64 || echo linux-x64) && \
    /tools/dotnet-ef migrations bundle -p src/Infrastructure -s src/Api --configuration Release \
    --self-contained -r "$rid" --force -o /bundle/efbundle

FROM mcr.microsoft.com/dotnet/runtime-deps:9.0 AS migrator
WORKDIR /app
COPY --from=migrator-build /bundle/efbundle .
USER $APP_UID
ENTRYPOINT ["./efbundle"]

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
# Uploaded files live on a volume; the non-root app user must own the mount point.
RUN mkdir -p /data/blobs && chown $APP_UID /data/blobs
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Api.dll"]
