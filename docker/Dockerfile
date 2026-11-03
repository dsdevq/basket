# basket - one image: the .NET API serves the Angular build from wwwroot.
#
#   1. web:     node builds the Angular app. @lifekit-hq/* come from GitHub Packages, so the build needs the
#               `npm_token` secret (a token with read:packages); it never lands in a layer.
#   2. api:     the .NET SDK publishes the API.
#   3. runtime: ASP.NET on Alpine, non-root, with tzdata for the Europe/Dublin Hangfire schedule.
#
# Build context: the repository root.
#   docker build -f docker/Dockerfile --secret id=npm_token,env=NODE_AUTH_TOKEN -t basket .

# --- 1. web ---------------------------------------------------------------------------------------
FROM node:22-alpine AS web
WORKDIR /web
COPY frontend/package.json frontend/package-lock.json frontend/.npmrc ./
RUN --mount=type=secret,id=npm_token \
    NODE_AUTH_TOKEN="$(cat /run/secrets/npm_token)" npm ci --no-audit --no-fund
COPY frontend/ ./
RUN npm run build:prod

# --- 2. api ---------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS api
WORKDIR /src
COPY global.json ./
COPY backend/Basket.sln backend/Directory.Build.props backend/Directory.Build.rsp backend/Directory.Packages.props backend/NuGet.Config ./backend/
COPY backend/src/Basket.Api/Basket.Api.csproj ./backend/src/Basket.Api/
RUN dotnet restore backend/src/Basket.Api/Basket.Api.csproj
COPY backend/src ./backend/src
RUN dotnet publish backend/src/Basket.Api/Basket.Api.csproj -c Release --no-restore -o /out

# --- 3. runtime -----------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
RUN apk add --no-cache tzdata \
    && mkdir /keys \
    && chown "$APP_UID" /keys
WORKDIR /app
COPY --from=api /out ./
COPY --from=web /web/dist/basket/browser ./wwwroot
ENV ASPNETCORE_HTTP_PORTS=3310 \
    DOTNET_gcServer=0 \
    DataProtection__KeyDirectory=/keys
USER $APP_UID
EXPOSE 3310
HEALTHCHECK --interval=30s --timeout=5s --start-period=40s --retries=3 \
    CMD wget -q -O /dev/null http://127.0.0.1:3310/api/health || exit 1
ENTRYPOINT ["dotnet", "Basket.Api.dll"]
