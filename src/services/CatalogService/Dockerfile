FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY src/services/CatalogService/Exvo.CatalogService.csproj src/services/CatalogService/
RUN dotnet restore src/services/CatalogService/Exvo.CatalogService.csproj

COPY src/services/CatalogService/ src/services/CatalogService/
RUN dotnet publish src/services/CatalogService/Exvo.CatalogService.csproj \
    --configuration Release \
    --output /app/publish \
    --no-restore \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Exvo.CatalogService.dll"]
