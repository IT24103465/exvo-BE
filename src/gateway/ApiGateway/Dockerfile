FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY src/gateway/ApiGateway/ApiGateway.csproj src/gateway/ApiGateway/
RUN dotnet restore src/gateway/ApiGateway/ApiGateway.csproj

COPY src/gateway/ApiGateway/ src/gateway/ApiGateway/
RUN dotnet publish src/gateway/ApiGateway/ApiGateway.csproj \
    --configuration Release \
    --output /app/publish \
    --no-restore \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "ApiGateway.dll"]
