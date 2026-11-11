# Parley web client.
#   ./scripts/fetch-reference-maps.sh     (once: installs the classic board into maps-local/)
#   docker build -t parley .
#   docker run -p 5080:5080 -v parley-data:/data parley
# maps-local/ is copied into the image when it exists; it holds third-party maps, so do not
# publish the image (see THIRD_PARTY.md). Without it the image offers only the Ring test maps.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish web/Parley.Web -c Release -o /app \
 && mkdir -p maps-local && mkdir -p /app/maps-local && cp -r maps-local/. /app/maps-local/

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
COPY maps ./maps
# Game.RepoRoot() looks for these two markers to find the maps folders
COPY PLAN.md ./PLAN.md
RUN mkdir engine
ENV PARLEY_DATA=/data PARLEY_URL=http://0.0.0.0:5080
EXPOSE 5080
ENTRYPOINT ["dotnet", "Parley.Web.dll"]
