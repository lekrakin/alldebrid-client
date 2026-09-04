FROM node:24-alpine3.22 AS frontend-build
WORKDIR /src/client

COPY client/package.json client/package-lock.json ./
RUN npm ci

COPY client/ ./
RUN npm run build -- --output-path=/out


FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS backend-build
WORKDIR /src

# x-release-please-start-version
ARG VERSION=1.6.0
# x-release-please-end

COPY server/AdbClient.sln server/Directory.Build.props ./server/
COPY server/AdbClient.Data/AdbClient.Data.csproj ./server/AdbClient.Data/
COPY server/AdbClient.Service/AdbClient.Service.csproj ./server/AdbClient.Service/
COPY server/AdbClient.Service.Test/AdbClient.Service.Test.csproj ./server/AdbClient.Service.Test/
COPY server/AdbClient.Web/AdbClient.Web.csproj ./server/AdbClient.Web/
RUN dotnet restore server/AdbClient.sln

COPY server/ ./server/
RUN dotnet test server/AdbClient.sln --configuration Release --no-restore \
    && dotnet publish server/AdbClient.Web/AdbClient.Web.csproj \
        --configuration Release \
        --no-restore \
        -p:Version="$VERSION" \
        -p:AssemblyVersion="$VERSION" \
        --output /out


FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS dotnet-runtime


FROM ghcr.io/linuxserver/baseimage-alpine:3.22

ARG BUILD_DATE
# x-release-please-start-version
ARG VERSION=1.6.0
# x-release-please-end

LABEL org.opencontainers.image.title="AllDebrid Client" \
      org.opencontainers.image.description="Self-hosted AllDebrid torrent manager" \
      org.opencontainers.image.version="$VERSION" \
      org.opencontainers.image.created="$BUILD_DATE" \
      org.opencontainers.image.source="https://github.com/krakn-dev/alldebrid-client" \
      org.opencontainers.image.licenses="MIT"

ENV DataPath="/data/db" \
    XDG_CONFIG_HOME="/config/xdg" \
    PATH="/usr/share/dotnet:$PATH"

RUN apk add --no-cache \
        bash \
        curl \
        icu-libs \
        krb5-libs \
        libgcc \
        libintl \
        libssl3 \
        libstdc++ \
        zlib \
    && mkdir -p /data/db /data/downloads \
    && chown abc:abc /data /data/db /data/downloads

COPY --from=dotnet-runtime /usr/share/dotnet /usr/share/dotnet

WORKDIR /app
COPY --from=backend-build /out/ ./
COPY --from=frontend-build /out/browser/ ./wwwroot/
COPY root/ /

EXPOSE 6500

HEALTHCHECK --interval=30s --timeout=10s --start-period=60s --retries=3 \
    CMD curl --fail --silent --show-error http://localhost:6500/health || exit 1
