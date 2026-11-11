# Parley web client. Build:  docker build -t parley .
# Run:    docker run -p 5080:5080 -v parley-data:/data parley
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish web/Parley.Web -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
COPY maps ./maps
# Game.RepoRoot() looks for these two markers to find the maps folder
COPY PLAN.md ./PLAN.md
RUN mkdir engine
ENV PARLEY_DATA=/data PARLEY_URL=http://0.0.0.0:5080
EXPOSE 5080
ENTRYPOINT ["dotnet", "Parley.Web.dll"]
