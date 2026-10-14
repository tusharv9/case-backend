# Use the official ASP.NET Core runtime as a parent image
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_HTTP_PORTS=8080
ENV HostIntegration__Mode=Standalone
ENV HostIntegration__AllowStandaloneInProduction=true
ENV Database__MigrateOnStartup=true
ENV Database__Seed=Development
ENV EnableSwagger=true

# Use the SDK image to build the application
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy the project file and restore dependencies
COPY ["CaseManagement.Api.csproj", "./"]
RUN dotnet restore "./CaseManagement.Api.csproj"

# Copy the rest of the source code
COPY . .
WORKDIR "/src/"
RUN dotnet build "CaseManagement.Api.csproj" -c Release -o /app/build

# Publish the application
FROM build AS publish
RUN dotnet publish "CaseManagement.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Build the final image
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .

# The environment variable for the connection string should be provided at runtime
# ENV ConnectionStrings__DefaultConnection="Your Neon DB Connection String Here"

ENTRYPOINT ["dotnet", "CaseManagement.Api.dll"]
