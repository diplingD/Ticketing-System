# --- Build stage: full .NET SDK, compiles and publishes the app ---
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy only the project file first and restore packages.
# This layer is cached and re-runs only when the .csproj changes (not on every code change).
COPY Ticketing.Api.csproj .
RUN dotnet restore

COPY . .
RUN dotnet publish -c Release -o /app --no-restore

# --- Runtime stage: only the ASP.NET Core runtime, no SDK (much smaller image) ---
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .

# The aspnet image listens on port 8080 by default and ships a non-root "app" user.
USER $APP_UID
ENTRYPOINT ["dotnet", "Ticketing.Api.dll"]
