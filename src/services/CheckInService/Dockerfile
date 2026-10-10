FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY src/services/CheckInService/Exvo.CheckInService.csproj src/services/CheckInService/
RUN dotnet restore src/services/CheckInService/Exvo.CheckInService.csproj

COPY src/services/CheckInService/ src/services/CheckInService/
RUN dotnet publish src/services/CheckInService/Exvo.CheckInService.csproj --configuration Release --output /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Exvo.CheckInService.dll"]
