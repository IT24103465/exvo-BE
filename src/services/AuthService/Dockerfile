FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY src/services/AuthService/ExvoAuthService.csproj src/services/AuthService/
RUN dotnet restore src/services/AuthService/ExvoAuthService.csproj

COPY src/services/AuthService/ src/services/AuthService/
RUN dotnet publish src/services/AuthService/ExvoAuthService.csproj \
    --configuration Release \
    --output /app/publish \
    --no-restore \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "ExvoAuthService.dll"]
