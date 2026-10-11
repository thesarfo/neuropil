# syntax=docker/dockerfile:1

FROM node:22-slim AS web
WORKDIR /src/web
COPY web/package.json web/package-lock.json ./
RUN npm ci
COPY web/ ./
# Vite writes the built page to ../src/Neuropil.Server/wwwroot.
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Neuropil.slnx ./
COPY src/ src/
COPY --from=web /src/src/Neuropil.Server/wwwroot src/Neuropil.Server/wwwroot
RUN dotnet publish src/Neuropil.Server -c Release -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out ./
COPY presets/ presets/
# Inside the container the server must listen on every interface; compose.yaml keeps the
# published port on this machine only.
ENV ASPNETCORE_URLS=http://0.0.0.0:5080
EXPOSE 5080
USER $APP_UID
ENTRYPOINT ["dotnet", "Neuropil.Server.dll"]
