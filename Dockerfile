# Build context is the platform's Backend/ folder, not this one:
#
#   cd Backend && docker build -f case-backend/Dockerfile -t omniconnect-case .
#
# Case Management compiles the platform's shared proxy edge (Backend/Shared/OmniConnect.Hosting) and
# Backend/Directory.Build.props, which exist only in the OmniConnect checkout — so, like Lead Management, the image is
# built from there. Backend/.dockerignore keeps every .env, bin/ and obj/ out of the image.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Manifests first, so the restore layer caches independently of source edits.
COPY Directory.Build.props ./
COPY Shared/OmniConnect.Hosting/OmniConnect.Hosting.csproj Shared/OmniConnect.Hosting/
COPY case-backend/CaseManagement.Api.csproj case-backend/
RUN dotnet restore case-backend/CaseManagement.Api.csproj

COPY Shared/OmniConnect.Hosting/ Shared/OmniConnect.Hosting/
COPY case-backend/ case-backend/
RUN dotnet publish case-backend/CaseManagement.Api.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

EXPOSE 8080
ENV ASPNETCORE_HTTP_PORTS=8080
# Unchanged from the earlier image; set them otherwise at runtime for a Host-integrated production deployment.
ENV HostIntegration__Mode=Standalone
ENV HostIntegration__AllowStandaloneInProduction=true
ENV Database__MigrateOnStartup=true
ENV Database__Seed=Development
ENV EnableSwagger=true
# The connection string is provided at runtime: ConnectionStrings__DefaultConnection.

# The service's own binary is its healthcheck (the aspnet image has no curl): OmniConnect.Hosting's --healthcheck mode.
HEALTHCHECK --interval=30s --timeout=8s --start-period=40s --retries=3 \
  CMD ["dotnet", "CaseManagement.Api.dll", "--healthcheck", "http://127.0.0.1:8080/health/live"]

ENTRYPOINT ["dotnet", "CaseManagement.Api.dll"]
